using System.Text;
using SteamReady;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new Exception("FAIL: expected rejection: " + name);
}
string Manifest(string id = "123", string mode = "0") =>
    "\"AppState\"\r\n{\r\n\t\"appid\"\t\"" + id + "\"\r\n\t\"name\"\t\"Fixture Game\"\r\n\t\"AutoUpdateBehavior\"\t\"" + mode +
    "\"\r\n\t\"Other\"\t\"keep me\"\r\n\t\"Nested\"\r\n\t{\r\n\t\t\"AutoUpdateBehavior\"\t\"1\"\r\n\t}\r\n}\r\n";

string original = Manifest();
string edited = SteamFiles.EditPreference(original, "123", "0", "2");
Check(edited == Manifest(mode: "2"), "Only top-level preference changes; CRLF and unrelated keys preserved");
Check(SteamFiles.EditPreference(edited, "123", "2", "0") == original, "Round trip");
Check(SteamFiles.EditPreference(original, "123", "0", "1") == Manifest(mode: "1"), "On-launch value");
Reject<InvalidOperationException>(() => SteamFiles.EditPreference(original, "999", "0", "2"), "Reject wrong app identity");
Reject<InvalidOperationException>(() => SteamFiles.EditPreference(original, "123", "1", "2"), "Reject stale preference");
Reject<ArgumentException>(() => SteamFiles.EditPreference(original, "123", "0", "3"), "Reject unsupported target");
Reject<FormatException>(() => SteamFiles.EditPreference(Manifest(mode: "9"), "123", "9", "2"), "Reject unknown stored preference");
Reject<FormatException>(() => SteamFiles.EditPreference(original.Replace("\t\"AutoUpdateBehavior\"\t\"0\"\r\n", ""), "123", "0", "2"), "Reject missing top-level preference");
Reject<FormatException>(() => SteamFiles.EditPreference(original.Replace("\t\"Other\"", "\t\"AutoUpdateBehavior\""), "123", "0", "2"), "Reject duplicate preference");
Reject<FormatException>(() => Vdf.Parse("\"AppState\" {"), "Reject unclosed object");
Reject<FormatException>(() => Vdf.Parse("\"AppState\" { bareword \"value\" }"), "Reject unsupported syntax");
Check(Vdf.Parse("// header\n\"key\" \"a\\\"b\"").Field("key")?.Value == "a\"b", "Comments and escaped quotes");

string root = Path.Combine(Path.GetTempPath(), "SteamReady-fixture-" + Guid.NewGuid().ToString("N"));
string main = Path.Combine(root, "main");
string other = Path.Combine(root, "other library");
string missing = Path.Combine(root, "offline drive");
string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
try
{
    Directory.CreateDirectory(Path.Combine(main, "steamapps"));
    Directory.CreateDirectory(Path.Combine(other, "steamapps"));
    File.WriteAllText(Path.Combine(main, "steam.exe"), "Fixture only");
    string folders = Path.Combine(main, "steamapps", "libraryfolders.vdf");
    File.WriteAllText(folders, "\"libraryfolders\" { \"0\" { \"path\" \"" + Esc(main) + "\" } \"1\" { \"path\" \"" + Esc(other) + "\" \"apps\" { \"456\" \"1\" } } \"2\" { \"path\" \"" + Esc(missing) + "\" } }");
    string app = Path.Combine(main, "steamapps", "appmanifest_123.acf");
    File.WriteAllText(app, original, new UTF8Encoding(true));
    File.WriteAllText(Path.Combine(other, "steamapps", "appmanifest_456.acf"), Manifest("456", "2"));
    File.WriteAllText(Path.Combine(other, "steamapps", "appmanifest_789.acf"), Manifest("999"));
    var scan = SteamFiles.Scan(main);
    Check(scan.Libraries.Count == 3, "Modern library discovery, main root deduplicated");
    Check(scan.Games.Count == 2 && scan.Games.Any(g => g.Id == "456" && g.Mode == "2"), "Multiple library scans and preference detection");
    Check(scan.Warnings.Count == 2, "Offline library and mismatched filename reported");
    Check(File.ReadAllBytes(app).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Scan does not change BOM");
    Check(File.ReadAllText(app) == original, "Scan does not modify manifest");
    File.WriteAllText(folders, "\"LibraryFolders\" { \"1\" \"" + Esc(other) + "\" }");
    Check(SteamFiles.Scan(main).Libraries.Count == 2, "Legacy library discovery");
    File.WriteAllText(folders, "malformed");
    var partial = SteamFiles.Scan(main);
    Check(partial.Games.Count == 1 && partial.Warnings.Count > 0, "Malformed library list reports partial scan");
    // All write tests use the isolated fixture and a fake Steam process check.
    string data = Path.Combine(root, "history-data");
    bool running = false;
    var history = new ChangeHistory(data, () => running);
    File.WriteAllText(folders, "\"libraryfolders\" { \"0\" { \"path\" \"" + Esc(main) + "\" } \"1\" { \"path\" \"" + Esc(other) + "\" } }");
    var fixture = SteamFiles.Scan(main);
    var firstGame = fixture.Games.Single(g => g.Id == "123");
    Check(history.Retention == 50, "Default history retention is 50");
    Reject<ArgumentOutOfRangeException>(() => history.SetRetention(49), "Reject retention below 50");
    history.SetRetention(51);
    Check(new ChangeHistory(data, () => false).Retention == 51, "Retention survives restart");
    running = true;
    Reject<InvalidOperationException>(() => history.Apply([firstGame], "2"), "No writes while Steam running");
    running = false;
    Check(history.List().Count == 0 && File.ReadAllText(app) == original, "Running Steam rejection leaves no history or manifest change");
    var result = history.Apply([firstGame], "2");
    Check(result.Changed == 1 && result.Errors.Count == 0, "Actual fixture write succeeds");
    var batch = history.List().Single();
    Check(batch.Entries.Single().State == "applied", "Successful write journaled");
    Check(File.ReadAllBytes(batch.Entries.Single().Backup).SequenceEqual(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(original))), "Original backup is byte exact");
    Check(File.ReadAllText(app) == edited && File.ReadAllBytes(app).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Actual write preserves BOM and unrelated content");
    Check(history.Apply([firstGame], "1").Errors.Count == 1 && File.ReadAllText(app) == edited, "Stale scanned preference rejected");
    // Simulate Steam changing unrelated manifest data after the first operation.
    File.WriteAllText(app, edited.Replace("keep me", "new Steam data"), new UTF8Encoding(true));
    var current = SteamFiles.Scan(main);
    Check(history.Restore(current, batch.Id, new HashSet<string> { "456" }).Changed == 0, "Nonselected game not restored");
    Check(history.Restore(current, batch.Id, new HashSet<string> { "123" }).Changed == 1, "Selective historical restore");
    Check(File.ReadAllText(app) == original.Replace("keep me", "new Steam data"), "Restore preserves newer Steam data");
    var restored = history.List().First();
    Check(restored.Kind == "restore" && restored.SourceBatchId == batch.Id && restored.Entries.Single().Before == "2", "Restoration creates its own history entry");
    Check(history.UndoLatest(SteamFiles.Scan(main)).Changed == 1, "Undo a restoration");
    Check(SteamFiles.Scan(main).Games.Single(g => g.Id == "123").Mode == "2", "Undo restoration returns to immediate");
    // Deliberate conflicting manual change must never be overwritten by old history.
    File.WriteAllText(app, Manifest(mode: "1"), new UTF8Encoding(true));
    var conflict = history.Restore(SteamFiles.Scan(main), batch.Id, null);
    Check(conflict.Changed == 0 && conflict.Errors.Count == 1 && File.ReadAllText(app) == Manifest(mode: "1"), "Historical restore rejects conflict");
    // A partial batch must not lose the successful entry.
    var good = SteamFiles.Scan(main).Games.Single(g => g.Id == "123");
    var bad = SteamFiles.Scan(main).Games.Single(g => g.Id == "456");
    File.Delete(bad.Path);
    var partialWrite = history.Apply([good, bad], "0");
    Check(partialWrite.Changed == 1 && partialWrite.Errors.Count == 1, "Partial batch reports successful and failed app");
    Check(history.List().First().Entries.Any(e => e.Id == "123" && e.State == "applied"), "Partial success remains recoverable");
    // Inject Steam starting between the durable intent and the final file replacement.
    int calls = 0;
    var race = new ChangeHistory(Path.Combine(root, "race-data"), () => ++calls >= 3);
    var beforeRace = SteamFiles.Scan(main).Games.Single(g => g.Id == "123");
    var raceResult = race.Apply([beforeRace], "2");
    Check(raceResult.Changed == 0 && raceResult.Errors.Count > 0 && SteamFiles.Scan(main).Games.Single(g => g.Id == "123").Mode == "0", "Steam restart before replace prevents write");
    Check(race.List().Single().Entries.Single().State == "failed", "Unchanged failed write recorded");
    // Retention operates on owned history directories only.
    var retained = new ChangeHistory(Path.Combine(root, "retention-data"), () => false);
    for (int n = 0; n < 52; n++)
    {
        var g = SteamFiles.Scan(main).Games.Single(g => g.Id == "123");
        var outcome = retained.Apply([g], g.Mode == "0" ? "2" : "0");
        if (outcome.Changed != 1 || outcome.Errors.Count != 0) throw new Exception("Retention fixture write failed");
    }
    Check(retained.List().Count == 50, "Oldest completed batches pruned to 50");
    Check(Directory.GetDirectories(Path.Combine(root, "retention-data", "history")).Length == 50, "Pruning removes owned backup directories");

    // Preferences use isolated local state; scans below are simulated snapshots.
    var prefs = new PreferenceStore(Path.Combine(root, "preference-data"));
    Game PGame(string id, string mode, string? location = null) =>
        new(Path.Combine(location ?? main, "steamapps", $"appmanifest_{id}.acf"), id, "Game " + id, location ?? main, mode);
    ScanResult PScan(params Game[] games) => new(main, [main, other], games.ToList(), []);
    Check(prefs.Observe(PScan(PGame("123", "2"))).Notices.Count == 0, "First scan establishes baseline without treating existing game as new");
    Check(prefs.Load().Games["123"].Preferred == "2", "Baseline remembers current preference");
    var incomplete = PScan() with { Warnings = ["Drive unavailable"] };
    Check(!prefs.Observe(incomplete).Complete && prefs.Load().Games["123"].Installed, "Incomplete scan does not invent uninstall");
    Check(!prefs.Observe(PScan(PGame("123", "2"), PGame("123", "0", other))).Complete, "Duplicate app ID scan is not trusted");
    prefs.Observe(PScan());
    Check(!prefs.Load().Games["123"].Installed && prefs.Load().Games["123"].Preferred == "2", "Uninstall retains saved preference");
    var reinstalled = PScan(PGame("123", "0", other));
    var reinstallNotice = prefs.Observe(reinstalled).Notices.Single();
    Check(reinstallNotice.Kind == "reinstall" && !reinstallNotice.RequiresConfirmation, "Reinstall detected by app ID on different drive; automatic restore default");
    var restarted = new PreferenceStore(Path.Combine(root, "preference-data"));
    Check(restarted.Observe(reinstalled, askOnReinstall: true).Notices.Single().RequiresConfirmation, "Pending reinstall survives restart and supports ask mode");
    Check(restarted.PlanSavedPreference(reinstalled, "123")?.Desired == "2", "Restore plan targets saved mode on new drive");
    Reject<InvalidOperationException>(() => restarted.PlanSavedPreference(PScan(PGame("123", "1", other)), "123"), "Reject stale restore plan");
    restarted.RememberApplied([new HistoryEntry("fixture", "123", "Game 123", "0", "2", "", "applied")]);
    Check(restarted.Observe(PScan(PGame("123", "2", other))).Notices.Count == 0, "Verified write clears pending without creating false manual exception");
    restarted.Observe(PScan(PGame("123", "1", other)));
    Check(restarted.Load().Games["123"] is { Preferred: "1", Exception: true }, "Default respects manual change as exception");
    var ask = restarted.Observe(PScan(PGame("123", "0", other)), askOnManualChange: true).Notices.Single();
    Check(ask.Kind == "manual" && ask.RequiresConfirmation && ask.Preferred == "1", "Ask mode preserves previous preference");
    Check(restarted.PlanSavedPreference(PScan(PGame("123", "0", other)), "123")?.Desired == "1", "Manual conflict can be restored");
    restarted.KeepObserved("123");
    Check(restarted.Load().Games["123"] is { Preferred: "0", Exception: true, Pending: null }, "Keep manual change resolves pending");
    var newGame = restarted.Observe(PScan(PGame("123", "0", other), PGame("456", "1"))).Notices.Single();
    Check(newGame.Kind == "new" && newGame.Id == "456", "New game recognized after baseline");
    restarted.KeepObserved("456");
    Reject<InvalidOperationException>(() => restarted.Forget("456"), "Cannot forget installed game");
    restarted.Observe(PScan(PGame("123", "0", other)));
    restarted.Forget("456");
    Check(!restarted.Load().Games.ContainsKey("456"), "Forget uninstalled game");
    Check(restarted.Observe(PScan(PGame("123", "0", other), PGame("456", "2"))).Notices.Single().Kind == "new", "Forgotten game treated as new");
    restarted.RememberApplied([new HistoryEntry("fixture", "123", "Game 123", "0", "2", "", "failed")]);
    Check(restarted.Load().Games["123"].Preferred == "0", "Failed writes do not change saved preference");
    Check(result.Applied?.Single().Id == "123" && partialWrite.Applied?.Count == 1, "History returns only verified successes for preference integration");

    // Group/profile state is isolated from earlier fixtures.
    var profileData = Path.Combine(root, "profile-data");
    var profiles = new ProfileStore(profileData);
    var profilePrefs = new PreferenceStore(profileData);
    var profileScan = PScan(PGame("123", "0"), PGame("456", "1"), PGame("789", "0"), PGame("999", "2"));
    profilePrefs.Observe(profileScan);
    var favorites = profiles.CreateGroup("Favorites", "2", ["123", "456"]);
    var large = profiles.CreateGroup("Large games", "1", ["123", "789"]);
    var normal = profiles.CreateProfile("Normal", null, new());
    var vacation = profiles.CreateProfile("Vacation", "1", new() { [favorites.Id] = "1", [large.Id] = "0" });
    var preview = profiles.Preview(normal.Id, profileScan, profilePrefs.Load());
    Check(preview.Decisions.Single(d => d.Game.Id == "123").Desired == "2", "Overlapping groups use explicit first-group priority");
    Check(preview.Decisions.Single(d => d.Game.Id == "789").Desired == "1", "Group default applies when not overridden");
    Check(preview.Decisions.Single(d => d.Game.Id == "999").Desired == "2", "Ungrouped game retains saved preference");
    profiles.SetGroupPriority([large.Id, favorites.Id]);
    Check(profiles.Preview(normal.Id, profileScan, profilePrefs.Load()).Decisions.Single(d => d.Game.Id == "123").Desired == "1", "Priority can be reordered");
    Reject<ArgumentException>(() => profiles.SetGroupPriority([large.Id, large.Id]), "Reject duplicate priority");
    var away = profiles.Preview(vacation.Id, profileScan, profilePrefs.Load());
    Check(away.Decisions.Single(d => d.Game.Id == "123").Desired == "0" && away.Decisions.Single(d => d.Game.Id == "999").Desired == "1", "Profile overrides group and ungrouped defaults");
    var copy = profiles.DuplicateProfile(vacation.Id, "Vacation copy");
    Check(copy.Id != vacation.Id && copy.GroupModes[large.Id] == "0", "Duplicate creates independent profile");
    var catalog = profiles.Load();
    catalog.Profiles.Single(p => p.Id == copy.Id).GroupModes[large.Id] = "2";
    profiles.Save(catalog);
    Check(new ProfileStore(profileData).Load().Profiles.Single(p => p.Id == vacation.Id).GroupModes[large.Id] == "0", "Duplicate independent and catalog persisted");
    Reject<FormatException>(() => profiles.CreateGroup("favorites", "2", []), "Duplicate group name rejected");
    Reject<FormatException>(() => profiles.CreateProfile("Bad mode", "9", new()), "Invalid profile mode rejected");
    Check(profiles.Load().Profiles.Count == 3, "Rejected edits preserve catalog");
    var manuallyChanged = PScan(PGame("123", "2"), PGame("456", "1"), PGame("789", "0"), PGame("999", "2"));
    Check(profiles.Preview(vacation.Id, manuallyChanged, profilePrefs.Load()).Decisions.Single(d => d.Game.Id == "123").Blocked, "Unobserved external change blocks profile");
    profilePrefs.Observe(manuallyChanged);
    Check(profiles.Preview(vacation.Id, manuallyChanged, profilePrefs.Load()).Decisions.Single(d => d.Game.Id == "123") is { Desired: "2", Reason: "Individual exception" }, "Individual exception wins over group and profile");
    Reject<InvalidOperationException>(() => profiles.Preview(normal.Id, incomplete, profilePrefs.Load()), "Incomplete scan blocks profile");
    profiles.DeleteGroup(large.Id);
    Check(profiles.Load().Profiles.All(p => !p.GroupModes.ContainsKey(large.Id)), "Deleting group removes dangling overrides");
    profiles.DeleteProfile(copy.Id);
    Check(profiles.Load().Profiles.Count == 2, "Delete inactive profile");

    // Real writes remain confined to temporary fixture manifests.
    string profileRoot = Path.Combine(root, "profile-steam");
    Directory.CreateDirectory(Path.Combine(profileRoot, "steamapps"));
    File.WriteAllText(Path.Combine(profileRoot, "steam.exe"), "Fixture only");
    File.WriteAllText(Path.Combine(profileRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {}");
    string papp = Path.Combine(profileRoot, "steamapps", "appmanifest_123.acf");
    string papp2 = Path.Combine(profileRoot, "steamapps", "appmanifest_456.acf");
    File.WriteAllText(papp, Manifest("123", "0"));
    File.WriteAllText(papp2, Manifest("456", "2"));
    var liveData = Path.Combine(root, "profile-live-data");
    var livePrefs = new PreferenceStore(liveData);
    var liveProfiles = new ProfileStore(liveData);
    var liveHistory = new ChangeHistory(liveData, () => false);
    var liveScan = SteamFiles.Scan(profileRoot);
    livePrefs.Observe(liveScan);
    var normalProfile = liveProfiles.CreateProfile("Normal", null, new());
    var awayProfile = liveProfiles.CreateProfile("Away", "1", new());
    var approved = liveProfiles.Preview(awayProfile.Id, liveScan, livePrefs.Load());
    Check(approved.Changes.Count == 2 && File.ReadAllText(papp) == Manifest("123", "0"), "Preview never writes manifest");
    Reject<InvalidOperationException>(() => liveProfiles.ApplyConfirmed(approved, PScan(PGame("123", "2")), livePrefs, liveHistory), "Stale preview rejected");
    var profileResult = liveProfiles.ApplyConfirmed(approved, SteamFiles.Scan(profileRoot), livePrefs, liveHistory);
    Check(profileResult.Changed == 2 && profileResult.Errors.Count == 0 && liveProfiles.Load().ActiveProfileId == awayProfile.Id, "Confirmed profile applies and activates");
    Check(liveHistory.List().Single().Kind == "profile" && liveHistory.List().Single().Entries.Count == 2, "Profile switch uses normal backup history");
    Check(livePrefs.Load().Games["123"] is { Preferred: "0", Observed: "1", ProfileTarget: "1" }, "Temporary profile keeps original individual preference");
    Check(livePrefs.Observe(SteamFiles.Scan(profileRoot)).Notices.Count == 0 && !livePrefs.Load().Games["123"].Exception, "Verified profile write does not create exception");
    var back = liveProfiles.Preview(normalProfile.Id, SteamFiles.Scan(profileRoot), livePrefs.Load());
    Check(liveProfiles.ApplyConfirmed(back, SteamFiles.Scan(profileRoot), livePrefs, liveHistory).Changed == 2, "Switching back restores distinct base preferences");
    Check(File.ReadAllText(papp) == Manifest("123", "0") && File.ReadAllText(papp2) == Manifest("456", "2"), "Original modes restored");
    Reject<InvalidOperationException>(() => liveProfiles.DeleteProfile(normalProfile.Id), "Cannot delete active profile");

}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine($"All {passed} fixture checks passed. No real Steam libraries accessed.");
