using System.Text.Json;
namespace SteamReady;

internal sealed record ManagementSettings(int Version = 1, bool Enabled = false, string Scope = "new",
    string DefaultMode = "2", string ApplyBehaviour = "manual", bool AskOnManualChange = false,
    bool AskOnReinstall = false, bool Notifications = false);
internal sealed record ManagementCandidate(Game Game, string Desired, string Reason, bool RequiresConfirmation);
internal sealed record ManagementReview(ManagementSettings Settings, bool Complete, bool SteamRunning,
    List<ManagementCandidate> Candidates);
internal sealed record ManagementTick(ManagementReview Review, ChangeResult? Result);

internal sealed class ManagementSettingsStore(string dataRoot)
{
    private string SettingsPath => Path.Combine(dataRoot, "management-settings.json");
    private static void Validate(ManagementSettings value)
    {
        if (value.Version != 1 || value.Scope is not ("new" or "continuous") ||
            value.DefaultMode is not ("0" or "1" or "2") ||
            value.ApplyBehaviour is not ("manual" or "auto" or "notify"))
            throw new FormatException("Invalid automatic management settings.");
    }
    public ManagementSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        var value = JsonSerializer.Deserialize<ManagementSettings>(File.ReadAllText(SettingsPath))
            ?? throw new FormatException("Invalid automatic management settings.");
        Validate(value);
        return value;
    }
    public void Save(ManagementSettings value)
    {
        Validate(value);
        Directory.CreateDirectory(dataRoot);
        string tmp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(tmp, SettingsPath, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}

// Called by the future foreground/tray controller on a serialized polling loop.
// Persistent observations are the queue; each poll rebuilds targets from current
// settings and a fresh scan, rather than replaying stale manifest paths.
internal sealed class AutomaticManager(string dataRoot, Func<bool> steamRunning, Func<ScanResult> scan)
{
    private ManagementSettingsStore Settings => new(dataRoot);
    private PreferenceStore Preferences => new(dataRoot);
    private ChangeHistory History => new(dataRoot, steamRunning);

    public ManagementReview Review()
    {
        var settings = Settings.Load();
        if (!settings.Enabled) return new(settings, true, steamRunning(), []);
        var current = scan();
        var observation = Preferences.Observe(current, settings.AskOnManualChange, settings.AskOnReinstall);
        if (!observation.Complete) return new(settings, false, steamRunning(), []);
        var state = Preferences.Load();
        var candidates = new List<ManagementCandidate>();
        foreach (var game in current.Games)
        {
            var saved = state.Games[game.Id];
            string? target = null;
            string reason = "";
            bool confirm = false;
            if (saved.Pending == "manual")
            {
                target = saved.Preferred; reason = "manual"; confirm = true;
            }
            else if (saved.Pending == "reinstall")
            {
                target = saved.Preferred; reason = "reinstall"; confirm = settings.AskOnReinstall;
            }
            else if (saved.Pending == "new")
            {
                target = saved.Exception ? saved.Preferred : settings.DefaultMode; reason = "new";
                if (target == game.Mode) Preferences.KeepObserved(game.Id);
            }
            else if (settings.Scope == "continuous" && !saved.Exception)
            {
                target = settings.DefaultMode; reason = "continuous";
            }
            if (target != null && target != game.Mode)
                candidates.Add(new(game, target, reason, confirm));
        }
        return new(settings, true, steamRunning(), candidates.OrderBy(c => c.Game.Id, StringComparer.Ordinal).ToList());
    }
    public ManagementTick Tick()
    {
        var review = Review();
        // Only the separately opted-in auto behaviour may write unattended.
        // Manual and notify modes expose candidates but do not apply.
        if (!review.Settings.Enabled || !review.Complete || review.SteamRunning ||
            review.Settings.ApplyBehaviour != "auto") return new(review, null);
        var eligible = review.Candidates.Where(c => !c.RequiresConfirmation).ToList();
        return eligible.Count == 0 ? new(review, null) : new(review, Execute(review, eligible, false));
    }
    public ChangeResult ApplyConfirmed(ManagementReview approved, IReadOnlySet<string> selectedIds)
    {
        if (selectedIds.Count == 0) return new(0, []);
        var selected = approved.Candidates.Where(c => selectedIds.Contains(c.Game.Id)).ToList();
        if (selected.Count != selectedIds.Count) throw new InvalidOperationException("Unknown pending app.");
        return Execute(approved, selected, true);
    }
    private ChangeResult Execute(ManagementReview approved, List<ManagementCandidate> selected, bool confirmed)
    {
        var fresh = Review();
        if (!fresh.Settings.Enabled || !fresh.Complete || fresh.SteamRunning ||
            fresh.Settings != approved.Settings || steamRunning())
            throw new InvalidOperationException("Steam or management settings changed; review again.");
        if (!confirmed && (fresh.Settings.ApplyBehaviour != "auto" || selected.Any(c => c.RequiresConfirmation)))
            throw new InvalidOperationException("Explicit approval required.");
        foreach (var candidate in selected)
            if (!fresh.Candidates.Contains(candidate))
                throw new InvalidOperationException("Pending preference changed; review again.");
        var result = History.ApplyManagement(selected.Select(c => new PlannedChange(c.Game, c.Desired)));
        try { Preferences.RememberApplied(result.Applied ?? []); }
        catch (Exception ex) { result.Errors.Add("Game preferences could not be saved: " + ex.Message); }
        return result;
    }
    // Keep a manual change or decline a pending reinstall/new preference.
    // For a continuous-policy candidate, explicitly remember the observed
    // value as an exception so the policy does not immediately requeue it.
    public void KeepCurrent(ManagementCandidate approved)
    {
        var fresh = Review();
        if (!fresh.Settings.Enabled || !fresh.Complete || !fresh.Candidates.Contains(approved))
            throw new InvalidOperationException("Pending preference changed; review again.");
        Preferences.KeepAsException(approved.Game.Id, approved.Game.Mode);
    }
}
