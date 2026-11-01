using System.IO;
using System.Diagnostics;
using HytaleAccounts;

namespace HytaleAccounts;

// Only used with a disposable copy of Setup: no process launch or installed path is reachable.
internal static class SettingsEntryProbe
{
    internal static bool Elevated;
    internal static int Removals;
    internal static int Handoffs;
    internal static bool Unavailable;
    internal static string Destination = "";
    internal static Process? Start(ProcessStartInfo start) => throw new Exception("Unexpected process launch: " + start.FileName);
    internal static void Handoff(string destination)
    {
        if (!Elevated || destination != Destination) throw new Exception("Invalid handoff boundary.");
        Handoffs++;
        if (Unavailable) throw new IOException("No matching user context.");
        Elevated = false;
        if (SettingsSetup.Run(true).GetAwaiter().GetResult() != 0) throw new Exception("Child entry failed.");
    }
    internal static Task Remove(string destination, Func<ProcessStartInfo, Task> integrate, Action<ProcessStartInfo> start)
    {
        if (Elevated) throw new Exception("Elevated entry reached removal without a user-context handoff.");
        if (destination != Destination) throw new Exception("Entry escaped disposable destination.");
        Removals++;
        return Task.CompletedTask;
    }
    internal static async Task<int> Run()
    {
        Destination = Path.Combine(Path.GetTempPath(), "settings-entry-" + Guid.NewGuid().ToString("N"));
        var failures = 0;
        foreach (var elevated in new[] { false, true })
        {
            Elevated = elevated; Removals = 0; Handoffs = 0;
            try
            {
                var result = await SettingsSetup.Run(true);
                if (result != 0 || Removals != 1 || Handoffs != (elevated ? 1 : 0)) throw new Exception("Entry did not reach safe removal exactly once.");
                Console.WriteLine("PASS " + (elevated ? "elevated Settings entry" : "ordinary unelevated entry"));
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL " + (elevated ? "elevated Settings entry" : "ordinary unelevated entry") + ": " + ex.Message);
            }
        }
        Elevated = true; Unavailable = true; Removals = 0; Handoffs = 0;
        try { await SettingsSetup.Run(true); failures++; }
        catch (IOException) { if (Removals != 0 || Handoffs != 1) failures++; else Console.WriteLine("PASS unavailable handoff preserves removal boundary"); }
        return failures == 0 ? 0 : 1;
    }
}
