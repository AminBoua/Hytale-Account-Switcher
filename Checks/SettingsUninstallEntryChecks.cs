using System.IO;
using System.Diagnostics;
using HytaleAccounts;

namespace HytaleAccounts;

// Only used with a disposable copy of Setup: no process launch or installed path is reachable.
internal static class SettingsEntryProbe
{
    internal static bool Elevated;
    internal static int Removals;
    internal static string Destination = "";
    internal static Process? Start(ProcessStartInfo start) => throw new Exception("Unexpected process launch: " + start.FileName);
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
            Elevated = elevated; Removals = 0;
            try
            {
                var result = await SettingsSetup.Run(true);
                if (result != 0 || Removals != 1) throw new Exception("Entry did not reach safe removal exactly once.");
                Console.WriteLine("PASS " + (elevated ? "elevated Settings entry" : "ordinary unelevated entry"));
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL " + (elevated ? "elevated Settings entry" : "ordinary unelevated entry") + ": " + ex.Message);
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
