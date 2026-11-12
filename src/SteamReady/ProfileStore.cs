using System.Text.Json;
namespace SteamReady;

// Group ordering is explicit: first matching group has the highest priority.
// Profiles override each group's default; individual exceptions always win.
internal sealed record GameGroup(string Id, string Name, string DefaultMode, List<string> AppIds);
internal sealed record GameProfile(string Id, string Name, string? UngroupedMode, Dictionary<string, string> GroupModes);
internal sealed record ProfileCatalog(int Version, List<GameGroup> Groups, List<GameProfile> Profiles, string? ActiveProfileId);
internal sealed record ProfileDecision(Game Game, string Desired, string Reason, bool Blocked);
internal sealed record ProfilePreview(string ProfileId, List<ProfileDecision> Decisions)
{
    public List<PlannedChange> Changes => Decisions.Where(d => !d.Blocked && d.Game.Mode != d.Desired)
        .Select(d => new PlannedChange(d.Game, d.Desired)).ToList();
}

internal sealed class ProfileStore(string dataRoot)
{
    private string StorePath => Path.Combine(dataRoot, "game-profiles.json");
    private static bool Mode(string? value) => value is "0" or "1" or "2";
    private static bool Id(string? value) => Guid.TryParseExact(value, "N", out _);
    private static bool AppId(string? value) => uint.TryParse(value, out uint n) && n > 0 &&
        n.ToString(System.Globalization.CultureInfo.InvariantCulture) == value;
    private static bool Name(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();
    private static void Validate(ProfileCatalog c)
    {
        if (c.Version != 1 || c.Groups == null || c.Profiles == null) throw new FormatException("Unsupported profile catalog.");
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in c.Groups)
        {
            if (g == null || !Id(g.Id) || !Name(g.Name) || !Mode(g.DefaultMode) || g.AppIds == null ||
                !groupIds.Add(g.Id) || !groupNames.Add(g.Name) ||
                g.AppIds.Any(id => !AppId(id)) || g.AppIds.Distinct(StringComparer.Ordinal).Count() != g.AppIds.Count)
                throw new FormatException("Invalid or duplicate group.");
        }
        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in c.Profiles)
        {
            if (p == null || !Id(p.Id) || !Name(p.Name) || !profileIds.Add(p.Id) || !profileNames.Add(p.Name) ||
                (p.UngroupedMode != null && !Mode(p.UngroupedMode)) || p.GroupModes == null ||
                p.GroupModes.Any(kv => !groupIds.Contains(kv.Key) || !Mode(kv.Value)))
                throw new FormatException("Invalid or duplicate profile.");
        }
        if (c.ActiveProfileId != null && !profileIds.Contains(c.ActiveProfileId))
            throw new FormatException("Active profile does not exist.");
    }
    public ProfileCatalog Load()
    {
        if (!File.Exists(StorePath)) return new(1, [], [], null);
        var catalog = JsonSerializer.Deserialize<ProfileCatalog>(File.ReadAllText(StorePath))
            ?? throw new FormatException("Invalid profile catalog.");
        Validate(catalog);
        return catalog;
    }
    public void Save(ProfileCatalog catalog)
    {
        // Validate before touching the previous file.
        Validate(catalog);
        Directory.CreateDirectory(dataRoot);
        string tmp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, catalog, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(tmp, StorePath, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    public GameGroup CreateGroup(string name, string defaultMode, IEnumerable<string> appIds)
    {
        var c = Load();
        var g = new GameGroup(Guid.NewGuid().ToString("N"), name.Trim(), defaultMode, appIds.Distinct(StringComparer.Ordinal).ToList());
        c.Groups.Add(g);
        Save(c);
        return g;
    }
    public GameProfile CreateProfile(string name, string? ungroupedMode, Dictionary<string, string> groupModes)
    {
        var c = Load();
        var p = new GameProfile(Guid.NewGuid().ToString("N"), name.Trim(), ungroupedMode, new(groupModes));
        c.Profiles.Add(p);
        Save(c);
        return p;
    }
    public GameProfile DuplicateProfile(string id, string name)
    {
        var original = Load().Profiles.Single(p => p.Id == id);
        return CreateProfile(name, original.UngroupedMode, original.GroupModes);
    }
    public void SetGroupPriority(IReadOnlyList<string> orderedIds)
    {
        var c = Load();
        if (orderedIds.Count != c.Groups.Count || orderedIds.Distinct(StringComparer.Ordinal).Count() != c.Groups.Count ||
            orderedIds.Any(id => !c.Groups.Any(g => g.Id == id)))
            throw new ArgumentException("Priority must list each group exactly once.");
        Save(c with { Groups = orderedIds.Select(id => c.Groups.Single(g => g.Id == id)).ToList() });
    }
    public void DeleteGroup(string id)
    {
        var c = Load();
        c.Groups.RemoveAll(g => g.Id == id);
        foreach (var p in c.Profiles) p.GroupModes.Remove(id);
        Save(c);
    }
    public void DeleteProfile(string id)
    {
        var c = Load();
        if (c.ActiveProfileId == id) throw new InvalidOperationException("Switch away from the active profile first.");
        c.Profiles.RemoveAll(p => p.Id == id);
        Save(c);
    }
    public ProfilePreview Preview(string id, ScanResult scan, PreferenceSnapshot preferences)
    {
        var c = Load();
        var p = c.Profiles.SingleOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Unknown profile.");
        if (scan.Warnings.Count != 0 || scan.Games.Any(g => !AppId(g.Id) || !Mode(g.Mode)) ||
            scan.Games.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != scan.Games.Count)
            throw new InvalidOperationException("A complete, unambiguous library scan is required.");
        var decisions = new List<ProfileDecision>();
        foreach (var game in scan.Games)
        {
            preferences.Games.TryGetValue(game.Id, out var saved);
            if (saved != null && (saved.Pending == "manual" || (saved.Installed && saved.Observed != game.Mode)))
            {
                decisions.Add(new(game, game.Mode, "Unresolved external preference change; observe or resolve first", true));
                continue;
            }
            if (saved?.Exception == true)
            {
                decisions.Add(new(game, saved.Preferred, "Individual exception", false));
                continue;
            }
            var group = c.Groups.FirstOrDefault(g => g.AppIds.Contains(game.Id, StringComparer.Ordinal));
            if (group != null)
            {
                string mode = p.GroupModes.TryGetValue(group.Id, out var value) ? value : group.DefaultMode;
                decisions.Add(new(game, mode, "Group: " + group.Name, false));
            }
            else
            {
                string target = p.UngroupedMode ?? saved?.Preferred ?? game.Mode;
                decisions.Add(new(game, target, p.UngroupedMode != null ? "Profile default" : "Saved individual preference", false));
            }
        }
        return new(id, decisions);
    }
    // A profile is not marked active until the user-confirmed preview matches
    // a fresh scan and all required writes verify. No Steam shutdown or auto-apply here.
    public ChangeResult ApplyConfirmed(ProfilePreview approved, ScanResult freshScan,
        PreferenceStore preferences, ChangeHistory history)
    {
        var fresh = Preview(approved.ProfileId, freshScan, preferences.Load());
        if (!fresh.Decisions.SequenceEqual(approved.Decisions))
            throw new InvalidOperationException("Profile preview changed; review it again.");
        if (fresh.Decisions.Any(d => d.Blocked))
            throw new InvalidOperationException("Resolve external preference changes before switching profiles.");
        var result = history.ApplyPlan(fresh.Changes, "profile", approved.ProfileId);
        // Keep base preferences separate from temporary profile targets.
        // Record verified successes even if other writes fail.
        try
        {
            preferences.RememberProfileApplied(result.Applied ?? []);
            if (result.Errors.Count == 0 && result.Changed == fresh.Changes.Count)
            {
                preferences.RememberProfileTargets(fresh.Decisions);
                var c = Load();
                Save(c with { ActiveProfileId = approved.ProfileId });
            }
        }
        catch (Exception ex) { result.Errors.Add("Profile state could not be saved: " + ex.Message); }
        return result;
    }
}
