using System.Text.Json;
using Microsoft.Win32;
namespace SteamReady;

internal sealed record RuntimeSettings(int Version = 1, bool BackgroundMode = false,
    bool StartWithWindows = false, bool StartMinimized = true);
internal sealed record QueuedGame(string Id, string Path, string Expected, string Desired);
internal sealed record PendingBatch(int Version, string Id, string Root, string Behaviour, bool NeedsReview, List<QueuedGame> Games);
internal sealed record BackgroundOutcome(string Status, ManagementReview? Review = null, ChangeResult? Result = null, string? Detail = null);

internal static class LocalState
{
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
internal sealed class RuntimeSettingsStore(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "runtime-settings.json");
    public RuntimeSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var s = JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(FilePath))
            ?? throw new FormatException("Invalid runtime settings.");
        if (s.Version != 1) throw new FormatException("Unsupported runtime settings.");
        return s;
    }
    public void Save(RuntimeSettings settings)
    {
        if (settings.Version != 1) throw new FormatException("Unsupported runtime settings.");
        LocalState.Save(FilePath, settings);
    }
    public static bool HideOnStartup(RuntimeSettings s, bool startupLaunch) =>
        startupLaunch && s.StartWithWindows && s.StartMinimized;
    public static bool HideOnClose(RuntimeSettings s, bool explicitExit, bool userClosing) =>
        s.BackgroundMode && !explicitExit && userClosing;
}

// Registry is modified only when the user explicitly saves startup settings.
// Merely launching SteamReady never registers it for startup.
internal static class WindowsStartup
{
    public static string Command(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires an absolute executable path.");
        return "\"" + Path.GetFullPath(executable) + "\" --startup";
    }
    public static void SetEnabled(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")
            ?? throw new IOException("Cannot open Windows startup registry.");
        if (enabled)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("SteamReady executable not found.", executable);
            key.SetValue("SteamReady", Command(executable), RegistryValueKind.String);
        }
        else key.DeleteValue("SteamReady", false);
    }
}

internal sealed class PendingBatchStore(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "pending-batch.json");
    private static bool Mode(string? value) => value is "0" or "1" or "2";
    private static void Validate(PendingBatch batch)
    {
        if (batch.Version != 1 || !Guid.TryParseExact(batch.Id, "N", out _) ||
            !Path.IsPathFullyQualified(batch.Root) || batch.Behaviour is not ("auto" or "notify") ||
            batch.Games == null || batch.Games.Count == 0 ||
            batch.Games.Any(g => g == null || !uint.TryParse(g.Id, out uint id) || id == 0 ||
                !Path.IsPathFullyQualified(g.Path) || !Mode(g.Expected) || !Mode(g.Desired) || g.Expected == g.Desired) ||
            batch.Games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != batch.Games.Count)
            throw new FormatException("Invalid pending batch.");
    }
    public PendingBatch? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var batch = JsonSerializer.Deserialize<PendingBatch>(File.ReadAllText(FilePath))
            ?? throw new FormatException("Invalid pending batch.");
        Validate(batch);
        return batch;
    }
    public void Save(PendingBatch batch) { Validate(batch); LocalState.Save(FilePath, batch); }
    public void Clear() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}

// A single serialized caller owns polling and explicit operations. An explicit
// batch has priority over policy changes, including while awaiting review.
internal sealed class BackgroundController(string dataRoot, Func<bool> steamRunning, Func<ScanResult> scan)
{
    private readonly object gate = new();
    private PendingBatchStore Queue => new(dataRoot);
    private ManagementSettingsStore Settings => new(dataRoot);
    private PreferenceStore Preferences => new(dataRoot);
    private ChangeHistory History => new(dataRoot, steamRunning);
    private static bool Complete(ScanResult s) => s.Warnings.Count == 0 &&
        s.Games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() == s.Games.Count;
    public PendingBatch? Pending { get { lock (gate) return Queue.Load(); } }

    // Only call after the user explicitly chooses to queue the selected batch.
    // Never replace existing work silently.
    public PendingBatch Enqueue(ScanResult approved, IEnumerable<Game> selected, string desired)
    {
        lock (gate)
        {
            var settings = Settings.Load();
            if (settings.ApplyBehaviour == "manual") throw new InvalidOperationException("Manual apply does not queue changes.");
            if (Queue.Load() != null) throw new InvalidOperationException("Cancel or resolve the existing pending batch first.");
            if (!Complete(approved) || desired is not ("0" or "1" or "2")) throw new InvalidOperationException("Invalid or incomplete selection.");
            var items = selected.Where(g => g.Mode != desired).ToArray();
            if (items.Length == 0 || items.Any(g => !approved.Games.Contains(g)))
                throw new InvalidOperationException("No valid selected changes.");
            var batch = new PendingBatch(1, Guid.NewGuid().ToString("N"), approved.Root, settings.ApplyBehaviour, false,
                items.Select(g => new QueuedGame(g.Id, g.Path, g.Mode, desired)).ToList());
            Queue.Save(batch);
            return batch;
        }
    }
    public void Cancel(string id)
    {
        lock (gate)
        {
            if (Queue.Load()?.Id != id) throw new InvalidOperationException("Pending batch changed.");
            Queue.Clear();
        }
    }
    private static List<PlannedChange> Revalidate(PendingBatch batch, ScanResult fresh)
    {
        if (!Complete(fresh) || !string.Equals(batch.Root, fresh.Root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Library scan incomplete or Steam installation changed.");
        var plan = new List<PlannedChange>();
        foreach (var item in batch.Games)
        {
            var game = fresh.Games.SingleOrDefault(g => g.Id == item.Id && string.Equals(g.Path, item.Path, StringComparison.OrdinalIgnoreCase));
            if (game == null || game.Mode != item.Expected)
                throw new InvalidOperationException("Pending app changed or moved: " + item.Id + ". Cancel and review a fresh selection.");
            plan.Add(new(game, item.Desired));
        }
        return plan;
    }
    public BackgroundOutcome Tick()
    {
        lock (gate)
        {
            try
            {
                var queued = Queue.Load();
                if (queued != null)
                {
                    if (queued.NeedsReview) return new("queue-review", Detail: "Previous attempt needs review; cancel and create a fresh batch.");
                    var settings = Settings.Load();
                    if (settings.ApplyBehaviour != queued.Behaviour) return new("queue-paused", Detail: "Apply behaviour changed.");
                    if (steamRunning()) return new("waiting-for-steam");
                    if (queued.Behaviour == "notify") return new("queue-ready");
                    return ApplyQueuedCore(queued, false);
                }
                var tick = new AutomaticManager(dataRoot, steamRunning, scan).Tick();
                return new(tick.Result == null ? "observed" : "managed", tick.Review, tick.Result);
            }
            catch (Exception ex) { return new("error", Detail: ex.Message); }
        }
    }
    public BackgroundOutcome ApplyConfirmed(string batchId)
    {
        lock (gate)
        {
            try
            {
                var batch = Queue.Load();
                if (batch == null || batch.Id != batchId || batch.NeedsReview)
                    throw new InvalidOperationException("Pending batch changed or needs review.");
                return ApplyQueuedCore(batch, true);
            }
            catch (Exception ex) { return new("error", Detail: ex.Message); }
        }
    }
    private BackgroundOutcome ApplyQueuedCore(PendingBatch batch, bool confirmed)
    {
        if (Settings.Load().ApplyBehaviour != batch.Behaviour || (!confirmed && batch.Behaviour != "auto"))
            return new("queue-paused", Detail: "Apply behaviour changed or confirmation required.");
        if (steamRunning()) return new("waiting-for-steam");
        List<PlannedChange> plan;
        try { plan = Revalidate(batch, scan()); }
        catch (Exception ex)
        {
            Queue.Save(batch with { NeedsReview = true });
            return new("queue-review", Detail: ex.Message);
        }
        // Mark before writing: a crash/partial failure never triggers blind replay.
        Queue.Save(batch with { NeedsReview = true });
        var result = History.ApplyQueued(plan);
        try { Preferences.RememberApplied(result.Applied ?? [], explicitChoice: true); }
        catch (Exception ex) { result.Errors.Add("Game preferences could not be saved: " + ex.Message); }
        if (result.Errors.Count == 0 && result.Changed == plan.Count)
        {
            Queue.Clear();
            return new("queue-applied", Result: result);
        }
        return new("queue-review", Result: result, Detail: "Some changes may have succeeded. Review history, cancel and create a fresh batch.");
    }
}
