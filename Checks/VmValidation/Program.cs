using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HytaleAccounts;
using Microsoft.Win32.SafeHandles;

// This entry point never calls Setup.Run, installs an addon, or launches a payload executable.
internal static class Program
{
    private const string Fake = "MOD-LAUNCHER-VM-SYNTHETIC-PAYLOAD";
    private static string root = "";
    private static JsonElement run;
    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private static bool Admin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static int Elevation
    {
        get
        {
            if (!GetTokenInformation(WindowsIdentity.GetCurrent().AccessToken, 18, out var type, 4, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return type;
        }
    }
    private static object Identity() => new { Sid, Administrator = Admin, ElevationType = Elevation, Pid = Environment.ProcessId, Utc = DateTime.UtcNow, DotnetRuntime = RuntimeInformation.FrameworkDescription };
    private static string Setting(string name) => run.GetProperty(name).GetString()!;
    private static string PathAt(params string[] parts) => Path.Combine([root, .. parts]);
    private static void Check(bool condition, string message) { if (!condition) throw new IOException(message); }
    private static void Write(string path, object value)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value); file.Flush(true);
    }
    private static async Task<JsonElement> Wait(string path)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(120))
        {
            if (File.Exists(path))
            {
                try { return JsonDocument.Parse(File.ReadAllBytes(path)).RootElement.Clone(); }
                catch (JsonException) { /* A tiny evidence file may still be being flushed. */ }
            }
            await Task.Delay(100);
        }
        throw new IOException("Timed out waiting for the independent VM actor: " + path);
    }
    public static async Task<int> Main(string[] args)
    {
        var guarded = false;
        try
        {
            Check(args.Length >= 2, "Usage: Checks.exe begin|attack|identity ROOT [normal|ots]");
            root = Path.GetFullPath(args[1]).TrimEnd('\\');
            var guard = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell/v1.0/powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(AppContext.BaseDirectory, "Guard.ps1"), "-ValidateRoot", root }) guard.ArgumentList.Add(arg);
            using (var process = Process.Start(guard)!)
            {
                var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync(); await output;
                Check(process.ExitCode == 0, "VM guard refused execution: " + await error);
            }
            Check(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\').Equals(PathAt("bin"), StringComparison.OrdinalIgnoreCase), "Run only the designated immutable package.");
            run = JsonDocument.Parse(File.ReadAllBytes(PathAt("run.json"))).RootElement.Clone();
            guarded = true;
            if (args[0] == "identity") { Console.WriteLine(JsonSerializer.Serialize(Identity())); return 0; }
            if (args[0] == "system-stage")
            {
                Check(Sid == "S-1-5-18" && Admin, "SYSTEM staging requires the real SYSTEM token.");
                var stage = PathAt("stage-system");
                using (var input = new MemoryStream(Encoding.UTF8.GetBytes(Fake)))
                using (var payload = SetupPayload.Extract(stage, input, _ => new MemoryStream([1, 2, 3]), SetupPayload.Permissions()))
                {
                    SetupPayload.RequireTrusted(new DirectoryInfo(stage).GetAccessControl());
                    Check(File.ReadAllText(Path.Combine(stage, "HytaleAccounts.exe")) == Fake, "SYSTEM cannot read the protected payload.");
                }
                Check(!Directory.Exists(stage), "SYSTEM staging cleanup failed.");
                Write(PathAt("evidence", "system-stage.json"), new { Success = true, Identity = Identity(), StagingRemoved = true });
                return 0;
            }
            Check(args.Length >= 3 && args[2] is "normal" or "ots", "Specify normal or ots.");
            var mode = args[2];
            if (args[0] == Setup.ElevatedMode)
            {
                Check(args.Length == 4, "Invalid elevation arguments.");
                await Elevated(mode, args[3]);
            }
            else if (args[0] == "begin") await Begin(mode);
            else if (args[0] == "attack") await Attack(mode);
            else throw new IOException("Unknown harness operation.");
            return 0;
        }
        catch (Exception ex)
        {
            if (guarded && args.Length > 2 && args[2] is "normal" or "ots")
            {
                try
                {
                    var phase = args[0] == Setup.ElevatedMode ? "elevated" : args[0] == "attack" ? "attack" : "begin";
                    var name = "failed-" + phase + "-" + args[2] + ".json";
                    Write(Admin ? PathAt("evidence", name) : Channel(Sid, name), new { Success = false, Identity = Identity(), Error = ex.Message });
                }
                catch (Exception) { /* A reporting failure must not turn a failed test into a pass. */ }
            }
            Console.Error.WriteLine("FAIL " + ex.Message); return 1;
        }
    }
    private static string Initiator(string mode) => Setting(mode == "normal" ? "AdminSid" : "UserSid");
    private static string Channel(string sid, string name) => PathAt("channels", sid, name);
    private static string Source(string mode) => Channel(Initiator(mode), "private-" + mode);
    private static async Task Begin(string mode)
    {
        Check(!Admin && Sid == Initiator(mode), "Begin must use the designated unelevated initiator.");
        Check(Elevation == (mode == "normal" ? 3 : 1), "Normal requires a split admin token; OTS requires a standard user token.");
        var source = Source(mode);
        Check(!Directory.Exists(source), "Each mode is single-use; create a new run after a failure.");
        PrivateFiles.CreateDirectory(source);
        NativeStores.AtomicWrite(Path.Combine(source, "source.bin"), Encoding.UTF8.GetBytes(Fake), privateFile: true);
        NativeStores.AtomicWrite(Path.Combine(source, "account.dat"), Encoding.UTF8.GetBytes("FAKE ACCOUNT ONLY"), privateFile: true);
        using var dirs = new SetupPayload.PinnedDirectories(source, false);
        using var file = SetupPayload.OpenSource(Path.Combine(source, "source.bin"));
        using var runnerDirs = new SetupPayload.PinnedDirectories(AppContext.BaseDirectory, false);
        using var runner = SetupPayload.OpenSource(Environment.ProcessPath!);
        Write(Channel(Sid, "begin-" + mode + ".json"), new { Identity = Identity(), Phase = "elevation-requested", Mode = mode });
        // Production UAC command construction; the harness receives --install-elevated, never the installer.
        using var child = Process.Start(Setup.ElevationStart(Environment.ProcessPath!, root, mode))!;
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Check(child.ExitCode == 0, "Elevated worker failed; inspect VM evidence. No installer was run.");
        Check(File.ReadAllText(Path.Combine(source, "source.bin")) == Fake, "Source victim changed across elevation.");
        Write(Channel(Sid, "completed-" + mode + ".json"), new { Success = true, Identity = Identity(), Mode = mode });
        Console.WriteLine("PASS " + mode + " UAC and guarded fake payload workflow; see evidence.");
    }
    private static async Task Elevated(string mode, string initiator)
    {
        Check(Admin && Elevation == 2 && Sid == Setting("AdminSid"), "Worker requires the designated admin's actual full UAC token.");
        Check(initiator == Initiator(mode) && (mode == "normal" ? Sid == initiator : Sid != initiator), "Wrong UAC identity transition.");
        var start = await Wait(Channel(initiator, "begin-" + mode + ".json"));
        Check(start.GetProperty("Identity").GetProperty("Sid").GetString() == initiator, "Initiator evidence mismatch.");
        var sourceAttack = await Wait(Channel(Setting("AttackerSid"), "source-" + mode + ".json"));
        Check(sourceAttack.GetProperty("Success").GetBoolean(), "Cross-user source attacks failed validation.");
        var sourceRejected = false;
        var approvedBytes = Encoding.UTF8.GetBytes(Fake);
        Check(new FileInfo(Path.Combine(Source(mode), "source.bin")).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value == initiator,
            "Synthetic downloaded source must be owned by the unelevated initiator.");
        try
        {
            using var dirs = new SetupPayload.PinnedDirectories(Source(mode), false);
            using var file = SetupPayload.OpenSource(Path.Combine(Source(mode), "source.bin"));
            using var buffer = new MemoryStream(); file.CopyTo(buffer);
            approvedBytes = buffer.ToArray();
            Check(Encoding.UTF8.GetString(approvedBytes) == Fake, "Source substitution detected.");
        }
        catch (IOException ex) when (mode == "ots" && (ex.Message.StartsWith("Unsafe owner of", StringComparison.Ordinal) || ex.Message.StartsWith("Unsafe mutation permissions", StringComparison.Ordinal)))
        { sourceRejected = true; }
        Check(sourceRejected == (mode == "ots"), "OTS user-owned source must fail closed under the other admin SID.");
        // OTS source rejection is a compatibility limit, not permission to weaken the production owner check.
        var stage = PathAt("stage-" + mode);
        var victim = PathAt("victim.txt");
        string acl;
        var fileAcls = new List<object>();
        using (var input = new MemoryStream(approvedBytes))
        using (var payload = SetupPayload.Extract(stage, input,
            name => new MemoryStream(Encoding.UTF8.GetBytes(name == "Install.ps1" ? "exit 23" : "FAKE RESOURCE " + name)), SetupPayload.Permissions()))
        {
            var security = new DirectoryInfo(stage).GetAccessControl();
            SetupPayload.RequireTrusted(security);
            Check(security.GetOwner(typeof(SecurityIdentifier))?.Value == "S-1-5-32-544" && security.AreAccessRulesProtected, "Production staging owner/protection mismatch.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Check(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Value is "S-1-5-18" or "S-1-5-32-544", "Staging has a non-administrator read grant.");
            acl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            foreach (var name in SetupPayload.Scripts.Concat(["HytaleAccounts.exe"]))
            {
                var permissions = new FileInfo(Path.Combine(stage, name)).GetAccessControl();
                SetupPayload.RequireTrusted(permissions);
                foreach (FileSystemAccessRule rule in permissions.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    Check(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference.Value is "S-1-5-18" or "S-1-5-32-544", "Payload has a non-administrator read grant.");
                fileAcls.Add(new { Name = name, Sddl = permissions.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) });
            }
            Write(PathAt("evidence", "stage-ready-" + mode + ".json"), new { Identity = Identity(), Stage = stage, Sddl = acl });
            var attack = await Wait(Channel(Setting("AttackerSid"), "stage-" + mode + ".json"));
            Check(attack.GetProperty("Success").GetBoolean(), "Cross-user staging attacks failed validation.");
            Check(File.ReadAllText(Path.Combine(stage, "HytaleAccounts.exe")) == Fake, "Staged payload changed.");
            using var failure = Process.Start(Setup.InstallStart(stage, root, root))!;
            await failure.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(failure.ExitCode == 23, "The only executed payload script must return synthetic failure 23.");
        }
        Check(!Directory.Exists(stage), "Staging survived failure cleanup.");
        var partial = PathAt("partial-" + mode);
        try
        {
            using var payload = SetupPayload.Extract(partial, new MemoryStream([1, 2]), _ => throw new IOException("synthetic missing resource"), SetupPayload.Permissions());
            throw new InvalidOperationException("Partial extraction was accepted.");
        }
        catch (IOException) { Check(!Directory.Exists(partial), "Partial extraction was not cleaned."); }
        var occupied = PathAt("occupied-" + mode);
        Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "victim.txt"), "VM VICTIM SENTINEL");
        try
        {
            using var payload = SetupPayload.Extract(occupied, new MemoryStream([1]), _ => new MemoryStream([2]), SetupPayload.Permissions());
            throw new InvalidOperationException("Existing staging was adopted.");
        }
        catch (IOException) { Check(File.ReadAllText(Path.Combine(occupied, "victim.txt")) == "VM VICTIM SENTINEL", "Existing staging victim was removed."); }
        var link = PathAt("linked-" + mode);
        Directory.CreateSymbolicLink(link, PathAt("victims"));
        try
        {
            try { using var pin = new SetupPayload.PinnedDirectories(link, true); throw new InvalidOperationException("Linked victim accepted."); }
            catch (IOException) { }
        }
        finally { Directory.Delete(link); }
        Check(File.ReadAllText(victim) == "VM VICTIM SENTINEL" && File.ReadAllText(PathAt("victims", "sentinel.txt")) == "VM VICTIM SENTINEL", "Victim sentinel changed.");
        Write(PathAt("evidence", "elevated-" + mode + ".json"), new { Success = true, Identity = Identity(), InitiatorSid = initiator,
            OtsForeignSourceRejected = sourceRejected, StagingSddl = acl, PayloadAcls = fileAcls, FailureExit = 23, StagingRemoved = true, VictimsPreserved = true });
    }
    private static async Task Attack(string mode)
    {
        Check(!Admin && Elevation == 1 && Sid == Setting("AttackerSid"), "Attack requires the unrelated designated standard user.");
        await Wait(Channel(Initiator(mode), "begin-" + mode + ".json"));
        // Positive controls ensure this is a live token performing real filesystem operations.
        var control = Channel(Sid, "control-" + mode);
        File.WriteAllText(control, "control"); Check(File.ReadAllText(control) == "control", "Attacker read/write positive control failed.");
        File.Move(control, control + "-moved"); File.Delete(control + "-moved");
        Probe("source", Source(mode), "source.bin");
        await Wait(PathAt("evidence", "stage-ready-" + mode + ".json"));
        Probe("stage", PathAt("stage-" + mode), "HytaleAccounts.exe");
        Console.WriteLine("PASS actual unrelated-user access denials: " + mode);

        void Probe(string phase, string directory, string name)
        {
            var rows = new List<object>();
            var path = Path.Combine(directory, name);
            void Denied(string operation, Action action)
            {
                try { action(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var code = ex.HResult & 0xffff;
                    Check(code is 5 or 32 or 33, operation + " failed for an unrelated reason, Windows error " + code);
                    rows.Add(new { Operation = operation, Denied = true, WindowsError = code }); return;
                }
                throw new IOException("Protection failure: unrelated user succeeded at " + operation);
            }
            Denied("read", () => File.ReadAllBytes(path));
            Denied("write", () => File.WriteAllText(path, "ATTACKER FAKE"));
            File.WriteAllText(control, "ATTACKER REPLACEMENT");
            try { Denied("replace", () => File.Move(control, path, true)); }
            finally { if (File.Exists(control)) File.Delete(control); }
            Denied("delete-leaf", () => File.Delete(path));
            Denied("rename-directory", () => Directory.Move(directory, directory + "-stolen"));
            Denied("insert-file", () => File.WriteAllText(Path.Combine(directory, "inserted.txt"), "ATTACKER FAKE"));
            Denied("cleanup-interference-handle", () => { using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); });
            Denied("delete-directory", () => Directory.Delete(directory, recursive: true));
            if (phase == "source")
            {
                Denied("account-read", () => File.ReadAllBytes(Path.Combine(directory, "account.dat")));
                Denied("elevated-runner-substitution", () => File.WriteAllText(PathAt("bin", "Checks.exe"), "ATTACKER FAKE"));
                Denied("helper-library-substitution", () => File.WriteAllText(PathAt("bin", "HytaleAccounts.dll"), "ATTACKER FAKE"));
            }
            Write(Channel(Sid, phase + "-" + mode + ".json"), new { Success = true, Identity = Identity(), PositiveControl = true, Operations = rows });
        }
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int size, out int returned);
}
