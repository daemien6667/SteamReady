using System.Text.Json;
namespace SteamReady;

// Local, app-ID-keyed preferences. Observation never edits Steam manifests.
// Pending decisions persist until explicitly resolved or a verified write succeeds.
internal sealed record SavedGame(string Id, string Name, string Preferred, string Observed, bool Installed,
    bool Exception, string? Pending);
internal sealed record PreferenceSnapshot(int Version, bool BaselineEstablished, Dictionary<string, SavedGame> Games);
internal sealed record PreferenceNotice(string Id, string Name, string Kind, string Observed, string Preferred, bool RequiresConfirmation);
internal sealed record PreferenceObservation(bool Complete, List<PreferenceNotice> Notices);

internal sealed class PreferenceStore(string dataRoot)
{
    private string StorePath => Path.Combine(dataRoot, "game-preferences.json");
    private static bool ValidMode(string? mode) => mode is "0" or "1" or "2";
    private static bool ValidId(string? id) => uint.TryParse(id, out uint value) && value > 0 && value.ToString(System.Globalization.CultureInfo.InvariantCulture) == id;
    private static void Validate(PreferenceSnapshot state)
    {
        if (state.Version != 1 || state.Games == null) throw new FormatException("Unsupported preferences format.");
        foreach (var (id, g) in state.Games)
        {
            if (g == null || !ValidId(id) || id != g.Id || g.Name == null || !ValidMode(g.Preferred) ||
                !ValidMode(g.Observed) || g.Pending is not (null or "reinstall" or "manual" or "new"))
                throw new FormatException("Invalid saved game preference: " + id);
        }
    }
    public PreferenceSnapshot Load()
    {
        if (!File.Exists(StorePath)) return new(1, false, new());
        var state = JsonSerializer.Deserialize<PreferenceSnapshot>(File.ReadAllText(StorePath))
            ?? throw new FormatException("Invalid preferences file.");
        Validate(state);
        return state;
    }
    private void Save(PreferenceSnapshot state)
    {
        Validate(state);
        Directory.CreateDirectory(dataRoot);
        string temp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temp, StorePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool Complete(ScanResult scan) => scan.Warnings.Count == 0 &&
        scan.Games.All(g => ValidId(g.Id) && ValidMode(g.Mode)) &&
        scan.Games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() == scan.Games.Count;

    public PreferenceObservation Observe(ScanResult scan, bool askOnManualChange = false, bool askOnReinstall = false)
    {
        // A disconnected drive, skipped manifest or duplicate app ID is not an uninstall.
        // Keep the entire previous snapshot untouched until a complete scan is available.
        if (!Complete(scan)) return new(false, []);
        var state = Load();
        var records = state.Games;
        var seen = scan.Games.ToDictionary(g => g.Id, StringComparer.Ordinal);
        foreach (var id in records.Keys.ToArray())
        {
            var old = records[id];
            if (!seen.ContainsKey(id)) records[id] = old with { Installed = false, Pending = null };
        }
        foreach (var g in scan.Games)
        {
            if (!records.TryGetValue(g.Id, out var old))
            {
                records[g.Id] = new(g.Id, g.Name, g.Mode, g.Mode, true, false, state.BaselineEstablished ? "new" : null);
                continue;
            }
            var next = old with { Name = g.Name, Installed = true, Observed = g.Mode };
            if (!old.Installed)
                next = next with { Pending = g.Mode == old.Preferred ? null : "reinstall" };
            else if (old.Pending == "reinstall")
            {
                if (g.Mode == old.Preferred) next = next with { Pending = null };
                else if (g.Mode != old.Observed)
                    next = next with { Pending = "manual" }; // changed again before restore: require a decision
            }
            else if (old.Pending == "manual")
            {
                if (g.Mode == old.Preferred) next = next with { Pending = null };
            }
            else if (g.Mode != old.Observed)
            {
                if (g.Mode == old.Preferred) next = next with { Pending = null };
                else if (askOnManualChange) next = next with { Pending = "manual" };
                else next = next with { Preferred = g.Mode, Exception = true, Pending = null };
            }
            records[g.Id] = next;
        }
        Save(state with { BaselineEstablished = true });
        return new(true, records.Values.Where(g => g.Installed && g.Pending != null)
            .OrderBy(g => g.Id, StringComparer.Ordinal)
            .Select(g => new PreferenceNotice(g.Id, g.Name, g.Pending!, g.Observed, g.Preferred,
                g.Pending == "manual" || (g.Pending == "reinstall" && askOnReinstall))).ToList());
    }
    // The caller supplies verified history outcomes only, never a requested target
    // for a failed or unverified write. Explicit changes also supersede pending work.
    public void RememberApplied(IEnumerable<HistoryEntry> entries, bool explicitChoice = false)
    {
        var applied = entries.Where(e => e.State == "applied").ToArray();
        if (applied.Length == 0) return;
        var state = Load();
        foreach (var e in applied)
        {
            state.Games.TryGetValue(e.Id, out var old);
            state.Games[e.Id] = new(e.Id, e.Name, e.After, e.After, true, explicitChoice || (old?.Exception ?? false), null);
        }
        Save(state);
    }
    public void KeepObserved(string id)
    {
        var state = Load();
        if (!state.Games.TryGetValue(id, out var game) || !game.Installed || game.Pending == null)
            throw new InvalidOperationException("No pending preference decision.");
        state.Games[id] = game with { Preferred = game.Observed, Exception = game.Exception || game.Pending == "manual", Pending = null };
        Save(state);
    }
    public void KeepAsException(string id, string expectedObserved)
    {
        var state = Load();
        if (!state.Games.TryGetValue(id, out var game) || !game.Installed || game.Observed != expectedObserved)
            throw new InvalidOperationException("Observed preference changed; rescan.");
        state.Games[id] = game with { Preferred = game.Observed, Exception = true, Pending = null };
        Save(state);
    }
    public void Forget(string id)
    {
        var state = Load();
        if (!state.Games.TryGetValue(id, out var game)) return;
        if (game.Installed) throw new InvalidOperationException("Only uninstalled games can be forgotten.");
        state.Games.Remove(id);
        Save(state);
    }
    // A plan only. Apply Changes Behaviour and the existing history writer will
    // govern execution; no direct manifest writes occur in this store.
    public PlannedChange? PlanSavedPreference(ScanResult freshScan, string id)
    {
        if (!Complete(freshScan)) throw new InvalidOperationException("A complete scan is required.");
        var state = Load();
        if (!state.Games.TryGetValue(id, out var saved) || !saved.Installed || saved.Pending is not ("manual" or "reinstall"))
            throw new InvalidOperationException("No saved preference awaiting restoration.");
        var game = freshScan.Games.SingleOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("Game no longer installed.");
        if (game.Mode != saved.Observed) throw new InvalidOperationException("Preference changed; observe again.");
        return game.Mode == saved.Preferred ? null : new(game, saved.Preferred);
    }
}
