using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HytaleAccounts;

internal sealed record PreparedImage(ProcessStartInfo Start, string Hash, string UpdateBase, string SourceHash, DateOnly? SourceDate);

internal static class LauncherImage
{
    private const int ProbePort = 49152;
    private const string ProbeCapability = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UpdateImage = "launcher-update.exe";
    private const string UpdateJournal = "launcher-update.json";
    private const string Executable = "hytale-launcher.exe";

    private sealed record SourceImage(byte[] Bytes, string Hash, DateOnly? Date);
    internal sealed record UpdateRecord(string ImageHash, string BaseHash);

    internal static string OfficialPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Hypixel Studios", "Hytale Launcher", Executable);

    internal static async Task ValidateOfficialAsync()
    {
        var official = await ReadOfficialAsync();
        _ = Patch(official.Bytes, ProbePort, ProbeCapability);
    }

    internal static void RequireIdle()
    {
        RequireStopped("hytale-launcher", "Close the Hytale launcher before starting the account addon.");
        RequireNoGame();
    }

    internal static void RequireNoGame() => RequireStopped("HytaleClient", "Close the game before switching accounts.");

    private static void RequireStopped(string name, string message)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            if (processes.Length != 0) throw new IOException(message);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal static async Task<PreparedImage> PrepareAsync(string accountsRoot, int port, string capability)
    {
        RequireIdle();
        using var accounts = new WindowsFiles.DirectoryPin(accountsRoot, forMutation: true);
        var official = await ReadOfficialAsync();
        var selected = official;
        var cache = Path.Combine(accountsRoot, UpdateImage);
        var record = ReadUpdate(accountsRoot);
        if (record is not null && WindowsFiles.FileExists(cache))
        {
            using var source = WindowsFiles.Open(cache);
            var hash = Hash(source);
            var date = BuildDate(cache);
            if (UseCachedUpdate(record, hash, date, official.Hash, official.Date))
            {
                await VerifyPublisherAsync(cache);
                source.Position = 0;
                selected = new SourceImage(await ReadAsync(source), hash, date);
            }
        }

        var bytes = Patch(selected.Bytes, port, capability);
        RequireIdle();
        var run = Path.Combine(accountsRoot, "run-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(run, Executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = run };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["HYTALE_LAUNCHER_NO_SELF_UPDATE"] = "1";
        var image = new PreparedImage(start, Hash(bytes), official.Hash, selected.Hash, selected.Date);
        WindowsPermissions.CreatePrivateDirectory(run);
        try
        {
            WindowsFiles.Write(executable, bytes, privateFile: true);
            if (HashFile(executable) != image.Hash) throw new IOException("The prepared launcher image failed verification.");
            return image;
        }
        catch
        {
            try { Delete(image); }
            catch (Exception error) { Trace.TraceWarning("Could not clean up the prepared launcher image. " + error.Message); }
            throw;
        }
    }

    internal static async Task<bool> PreserveUpdateAsync(string accountsRoot, PreparedImage image)
    {
        using var accounts = new WindowsFiles.DirectoryPin(accountsRoot, forMutation: true);
        var executable = image.Start.FileName;
        if (!WindowsFiles.FileExists(executable)) return false;
        using var run = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(executable)!, forMutation: true);
        using var source = WindowsFiles.Open(executable);
        var hash = Hash(source);
        if (hash == image.Hash || hash == image.SourceHash) return false;
        await VerifyPublisherAsync(executable);
        var date = BuildDate(executable);
        if (!IsUpdate(hash, date, image.SourceHash, image.SourceDate)) return false;
        source.Position = 0;
        var bytes = await ReadAsync(source);
        _ = Patch(bytes, ProbePort, ProbeCapability);

        var cache = Path.Combine(accountsRoot, UpdateImage);
        var oldRecord = ReadUpdate(accountsRoot);
        var newRecord = new UpdateRecord(hash, image.UpdateBase);
        if (oldRecord is not null && WindowsFiles.FileExists(cache))
        {
            using var previous = WindowsFiles.Open(cache);
            var previousHash = Hash(previous);
            if (oldRecord.ImageHash == previousHash && (oldRecord == newRecord || BuildDate(cache) > date))
                return false;
        }
        WindowsFiles.Write(cache, bytes, privateFile: true);
        WindowsFiles.Write(Path.Combine(accountsRoot, UpdateJournal), JsonSerializer.SerializeToUtf8Bytes(newRecord), privateFile: true);
        return true;
    }

    private static UpdateRecord? ReadUpdate(string accountsRoot)
    {
        var journal = Path.Combine(accountsRoot, UpdateJournal);
        if (!WindowsFiles.FileExists(journal)) return null;
        using var file = WindowsFiles.Open(journal);
        if (file.Length is 0 or > 4096) return null;
        try { return JsonSerializer.Deserialize<UpdateRecord>(file); }
        catch (JsonException) { return null; }
    }

    internal static bool UseCachedUpdate(UpdateRecord record, string candidateHash, DateOnly? candidateDate, string officialHash, DateOnly? officialDate) =>
        record.ImageHash == candidateHash && candidateHash != officialHash && candidateDate is not null && officialDate is not null &&
        (candidateDate > officialDate || (candidateDate == officialDate && record.BaseHash == officialHash));

    internal static bool IsUpdate(string candidateHash, DateOnly? candidateDate, string sourceHash, DateOnly? sourceDate) =>
        candidateHash != sourceHash && candidateDate is not null && sourceDate is not null && candidateDate >= sourceDate;

    internal static DateOnly? ParseDate(string? version) =>
        DateOnly.TryParseExact(version?.Split('-')[0], "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static DateOnly? BuildDate(string path) => ParseDate(FileVersionInfo.GetVersionInfo(path).ProductVersion);

    internal static string HashFile(string path)
    {
        using var parent = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = WindowsFiles.Open(path);
        return Hash(file);
    }

    private static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static void Delete(PreparedImage image)
    {
        var path = image.Start.FileName;
        var run = Path.GetDirectoryName(path)!;
        if (!WindowsFiles.DirectoryExists(run)) return;
        WindowsFiles.Delete(path);
        WindowsFiles.DeleteEmptyDirectory(run);
    }

    private static async Task<SourceImage> ReadOfficialAsync()
    {
        var path = OfficialPath;
        using var directory = new WindowsFiles.DirectoryPin(Path.GetDirectoryName(path)!, machine: true);
        using var source = WindowsFiles.Open(path);
        WindowsPermissions.RequireMachine(source.GetAccessControl(), path);
        await VerifyPublisherAsync(path);
        var bytes = await ReadAsync(source);
        return new SourceImage(bytes, Hash(bytes), BuildDate(path));
    }

    private static async Task<byte[]> ReadAsync(FileStream source)
    {
        var bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(bytes);
        return bytes;
    }

    private static async Task VerifyPublisherAsync(string path)
    {
        // Keep Windows trust evaluation in a bounded, invisible process.
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var script = "$ErrorActionPreference='Stop'; " +
            "$path=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPath + "')); " +
            @"$signature=Microsoft.PowerShell.Security\Get-AuthenticodeSignature -LiteralPath $path; " +
            "if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo(0,$false) -cne 'Hypixel Studios Canada Inc') { exit 1 }; exit 0";
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        var start = new ProcessStartInfo(Path.Combine(shell, "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory
        };
        start.Environment["PSModulePath"] = Path.Combine(shell, "Modules");
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var check = Process.Start(start) ?? throw new IOException("Could not start the launcher publisher check.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await check.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException error)
        {
            try { check.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (check.HasExited) { }
            throw new IOException("The launcher publisher check timed out.", error);
        }
        if (check.ExitCode != 0) throw new IOException("The launcher signature or Hypixel Studios Canada Inc publisher could not be verified.");
    }

    internal static byte[] Patch(byte[] original, int port, string capability, NativeAdapter.Fingerprints? fingerprints = null)
    {
        if (port is < 1024 or > 65535 || capability.Length != 43 ||
            capability.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new IOException("Invalid bridge bootstrap.");
        var text = Encoding.Latin1.GetString(original);
        foreach (var hook in new[] { "window.go.account.Service.CheckNetworkMode", "window.go.account.Service.GenerateOauthURL",
            "window.go.account.Service.IsUserLoggedIn", "window.go.account.Service.LogoutUser", "window.go.account.Service.HasValidSession",
            "launch-game__profile-dropdown", "hy-dropdown__list", "login__container", "settings__action-button", "getUserProfile",
            "launch-game__play-hytale", "hybutton-split__main" })
            if (!text.Contains(hook, StringComparison.Ordinal)) throw new IOException("Updated launcher frontend account interface changed.");
        const string title = "<title>Hytale Launcher</title>";
        var titleAt = text.IndexOf(title, StringComparison.Ordinal);
        if (titleAt < 0 || text.LastIndexOf(title, StringComparison.Ordinal) != titleAt) throw new IOException("Ambiguous embedded frontend.");
        var start = text.LastIndexOf("<html", titleAt, StringComparison.Ordinal);
        var end = text.IndexOf("</html>", titleAt, StringComparison.Ordinal);
        if (start < 0 || end < titleAt) throw new IOException("Missing embedded frontend boundary.");
        end += "</html>".Length;
        // Keep the native asset references and reuse space occupied by preload hints and whitespace.
        var html = Regex.Replace(text[start..end], "<link\\b[^>]*rel=\"modulepreload\"[^>]*>", "", RegexOptions.CultureInvariant);
        html = Regex.Replace(html, @">\s+<", "><", RegexOptions.CultureInvariant);
        const string openHead = "<head>";
        const string closeBody = "</body>";
        var headStart = html.IndexOf(openHead, StringComparison.Ordinal);
        var headEnd = html.IndexOf("</head>", StringComparison.Ordinal);
        var bodyEnd = html.IndexOf(closeBody, StringComparison.Ordinal);
        const string playGate = "<style>html:not([data-account-ready]) .launch-game__play-hytale{display:none!important}</style>";
        var script = $"<script src=\"http://127.0.0.1:{port}/{capability}/ui.js\"></script>";
        if (headStart < 0 || html.LastIndexOf(openHead, StringComparison.Ordinal) != headStart || headEnd <= headStart || bodyEnd <= headEnd ||
            html.LastIndexOf(closeBody, StringComparison.Ordinal) != bodyEnd || html.Length + playGate.Length + script.Length > end - start)
            throw new IOException("Updated launcher frontend has no room for the addon bootstrap.");
        html = html.Insert(bodyEnd, script).Insert(headStart + openHead.Length, playGate);
        var patched = (byte[])original.Clone();
        NativeAdapter.Apply(patched, fingerprints);
        patched.AsSpan(start, end - start).Fill((byte)' ');
        Encoding.Latin1.GetBytes(html).CopyTo(patched, start);
        return patched;
    }
}
