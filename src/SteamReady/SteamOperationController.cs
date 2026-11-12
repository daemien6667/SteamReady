using System.Diagnostics;
using Microsoft.Win32;
namespace SteamReady;

internal enum SteamClientState { Closed, Idle, Busy, Unknown }
internal interface ISteamClient
{
    SteamClientState Probe();
    void RequestShutdown();
    void Launch();
}
internal sealed record SteamOperation(string Status, ChangeResult? Result, bool Relaunched, string? Detail = null);

// Only an explicitly initiated operation can request shutdown or relaunch.
// Background management uses its own closed-client-only writer.
internal sealed class SteamOperationController(ISteamClient client, Func<TimeSpan, Task>? delay = null,
    int maxPolls = 60)
{
    private int active;
    private readonly Func<TimeSpan, Task> pause = delay ?? (duration => Task.Delay(duration));

    public async Task<SteamOperation> RunAsync(Func<bool> confirmShutdown, Func<ChangeResult> apply)
    {
        if (Interlocked.Exchange(ref active, 1) != 0) return new("busy", null, false, "Another operation is in progress.");
        bool restart = false;
        ChangeResult? result = null;
        string status = "applied";
        string? detail = null;
        bool relaunched = false;
        try
        {
            var initial = client.Probe();
            if (initial == SteamClientState.Busy || initial == SteamClientState.Unknown)
                return new("manual-required", null, false, "Close running games and exit Steam manually; client safety could not be established.");
            if (initial == SteamClientState.Idle)
            {
                // Never persist or infer consent, even if a prior operation was approved.
                if (!confirmShutdown()) return new("declined", null, false);
                var recheck = client.Probe();
                if (recheck == SteamClientState.Closed)
                {
                    // Steam exited independently: do not reopen it automatically.
                }
                else if (recheck != SteamClientState.Idle)
                    return new("manual-required", null, false, "Steam state changed; exit manually.");
                else
                {
                    client.RequestShutdown();
                    bool closed = false;
                    for (int i = 0; i < maxPolls; i++)
                    {
                        if (client.Probe() == SteamClientState.Closed) { closed = true; break; }
                        await pause(TimeSpan.FromMilliseconds(500));
                    }
                    if (!closed && client.Probe() != SteamClientState.Closed)
                        return new("timeout", null, false, "Steam did not exit in time. No changes applied; close Steam manually.");
                    restart = true;
                }
            }
            if (client.Probe() != SteamClientState.Closed)
                return new("manual-required", null, false, "Steam is active again; no changes applied.");
            try
            {
                result = apply(); // Existing writer rechecks Steam and verifies manifest bytes.
                status = result.Errors.Count == 0 ? "applied" : "partial-or-error";
            }
            catch (Exception ex) { status = "apply-error"; detail = ex.Message; }
            if (restart)
            {
                try
                {
                    // Do not launch a second instance if Steam restarted externally.
                    var now = client.Probe();
                    if (now == SteamClientState.Closed) { client.Launch(); relaunched = true; }
                    else if (now != SteamClientState.Idle && now != SteamClientState.Busy)
                        detail = (detail == null ? "" : detail + " ") + "Steam state uncertain; reopen manually.";
                }
                catch (Exception ex)
                {
                    detail = (detail == null ? "" : detail + " ") + "Steam relaunch failed: " + ex.Message;
                    status = "relaunch-error";
                }
            }
            return new(status, result, relaunched, detail);
        }
        catch (Exception ex) { return new("client-error", result, false, ex.Message); }
        finally { Volatile.Write(ref active, 0); }
    }
    public SteamOperation LaunchSteam()
    {
        if (Interlocked.Exchange(ref active, 1) != 0) return new("busy", null, false);
        try
        {
            var state = client.Probe();
            if (state == SteamClientState.Idle || state == SteamClientState.Busy) return new("already-running", null, false);
            if (state != SteamClientState.Closed) return new("manual-required", null, false, "Steam state uncertain.");
            client.Launch();
            return new("launched", null, true);
        }
        catch (Exception ex) { return new("client-error", null, false, ex.Message); }
        finally { Volatile.Write(ref active, 0); }
    }
}

// Windows adapter: registry indicators are heuristics, not a guaranteed game
// detector. Refuse unknown client state; also look for accessible game processes
// under the scanned libraries. Consent must tell the user to close all games.
internal sealed class WindowsSteamClient(string root, IReadOnlyList<string> libraries) : ISteamClient
{
    private string Exe => Path.GetFullPath(Path.Combine(root, "steam.exe"));
    public SteamClientState Probe()
    {
        var clients = Process.GetProcessesByName("steam");
        var helpers = Process.GetProcessesByName("steamwebhelper");
        try
        {
            if (clients.Length == 0) return helpers.Length == 0 ? SteamClientState.Closed : SteamClientState.Unknown;
            if (clients.Length != 1) return SteamClientState.Unknown;
            using var self = Process.GetCurrentProcess();
            var p = clients[0];
            if (p.SessionId != self.SessionId || !string.Equals(p.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                return SteamClientState.Unknown;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("RunningAppID") is not int runningId) return SteamClientState.Unknown;
            if (runningId != 0) return SteamClientState.Busy;
            using var apps = key.OpenSubKey("Apps");
            if (apps == null) return SteamClientState.Unknown;
            foreach (string id in apps.GetSubKeyNames())
            {
                using var app = apps.OpenSubKey(id);
                if (app?.GetValue("Running") is int running && running != 0) return SteamClientState.Busy;
            }
            var gameRoots = libraries.Select(l => Path.GetFullPath(Path.Combine(l, "steamapps", "common")) + Path.DirectorySeparatorChar).ToArray();
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    string? executable;
                    try { executable = process.MainModule?.FileName; }
                    catch { continue; } // Protected system processes are not inspectable.
                    if (executable != null && gameRoots.Any(path => executable.StartsWith(path, StringComparison.OrdinalIgnoreCase)))
                        return SteamClientState.Busy;
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return SteamClientState.Idle;
        }
        catch { return SteamClientState.Unknown; }
        finally
        {
            foreach (var p in clients) p.Dispose();
            foreach (var p in helpers) p.Dispose();
        }
    }
    private void Start(bool shutdown)
    {
        if (!File.Exists(Exe)) throw new FileNotFoundException("Steam executable not found.", Exe);
        var info = new ProcessStartInfo(Exe) { WorkingDirectory = Path.GetDirectoryName(Exe)!, UseShellExecute = false };
        if (shutdown) info.ArgumentList.Add("-shutdown");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Steam process did not start.");
    }
    public void RequestShutdown()
    {
        if (Probe() != SteamClientState.Idle) throw new InvalidOperationException("Steam is not safely idle.");
        Start(true);
    }
    public void Launch()
    {
        if (Probe() != SteamClientState.Closed) throw new InvalidOperationException("Steam is already active or state is uncertain.");
        Start(false);
    }
}
