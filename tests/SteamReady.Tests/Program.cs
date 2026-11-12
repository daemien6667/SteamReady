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

    string autoRoot = Path.Combine(root, "auto-steam");
    Directory.CreateDirectory(Path.Combine(autoRoot, "steamapps"));
    File.WriteAllText(Path.Combine(autoRoot, "steam.exe"), "Fixture only");
    File.WriteAllText(Path.Combine(autoRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {}");
    string autoApp(string id) => Path.Combine(autoRoot, "steamapps", $"appmanifest_{id}.acf");
    File.WriteAllText(autoApp("123"), Manifest("123", "0"));
    string autoData = Path.Combine(root, "auto-data");
    var config = new ManagementSettingsStore(autoData);
    var autoPrefs = new PreferenceStore(autoData);
    bool clientRunning = false;
    var manager = new AutomaticManager(autoData, () => clientRunning, () => SteamFiles.Scan(autoRoot));
    Check(config.Load() is { Enabled: false, Scope: "new", ApplyBehaviour: "manual" }, "Automatic management defaults off and manual");
    Check(manager.Tick().Result == null && !autoPrefs.Load().BaselineEstablished, "Disabled management does not scan or apply");
    config.Save(new(Enabled: true));
    Check(manager.Review().Candidates.Count == 0, "First enabled scan establishes baseline without changing existing apps");
    File.WriteAllText(autoApp("456"), Manifest("456", "0"));
    clientRunning = true;
    var pending = manager.Tick();
    Check(pending.Review.Candidates.Single().Reason == "new" && pending.Result == null, "New app queued while Steam running");
    clientRunning = false;
    Check(manager.Tick().Result == null && File.ReadAllText(autoApp("456")) == Manifest("456", "0"), "Manual default never applies automatically");
    config.Save(config.Load() with { ApplyBehaviour = "notify" });
    var notify = manager.Tick();
    Check(notify.Review.Candidates.Count == 1 && notify.Result == null, "Notify mode waits for confirmation");
    var accepted = manager.ApplyConfirmed(notify.Review, new HashSet<string> { "456" });
    Check(accepted.Changed == 1 && accepted.Errors.Count == 0 && manager.Review().Candidates.Count == 0, "Confirmed queue applies via history");
    Check(autoPrefs.Load().Games["456"].Preferred == "2" && !autoPrefs.Load().Games["456"].Exception, "Automatic management records verified preference without exception");
    config.Save(config.Load() with { ApplyBehaviour = "auto" });
    File.WriteAllText(autoApp("789"), Manifest("789", "0"));
    clientRunning = true;
    Check(manager.Tick().Result == null, "Auto mode never writes while Steam runs");
    clientRunning = false;
    Check(manager.Tick().Result?.Changed == 1 && File.ReadAllText(autoApp("789")) == Manifest("789", "2"), "Opt-in auto mode applies after closure");
    Check(File.ReadAllText(autoApp("123")) == Manifest("123", "0"), "New-only mode leaves baseline app unchanged");
    config.Save(config.Load() with { Scope = "continuous" });
    Check(manager.Tick().Result?.Changed == 1 && File.ReadAllText(autoApp("123")) == Manifest("123", "2"), "Continuous mode manages existing apps");
    File.WriteAllText(autoApp("123"), Manifest("123", "1"));
    Check(manager.Tick().Result == null && autoPrefs.Load().Games["123"].Exception, "External change respected as exception");
    config.Save(config.Load() with { AskOnManualChange = true });
    File.WriteAllText(autoApp("789"), Manifest("789", "1"));
    var manual = manager.Tick();
    Check(manual.Result == null && manual.Review.Candidates.Single().RequiresConfirmation, "Ask mode blocks unattended reversal of external change");
    manager.KeepCurrent(manual.Review.Candidates.Single());
    Check(autoPrefs.Load().Games["789"] is { Preferred: "1", Exception: true }, "Keep current resolves external decision");
    File.Delete(autoApp("789"));
    manager.Review();
    File.WriteAllText(autoApp("789"), Manifest("789", "0"));
    config.Save(config.Load() with { AskOnReinstall = true });
    var reinstall = manager.Tick();
    Check(reinstall.Result == null && reinstall.Review.Candidates.Single().Reason == "reinstall", "Reinstall ask setting requires approval");
    config.Save(config.Load() with { AskOnReinstall = false });
    Check(manager.Tick().Result?.Changed == 1 && File.ReadAllText(autoApp("789")) == Manifest("789", "1"), "Default reinstall restores saved exception");
    File.WriteAllText(autoApp("999"), Manifest("999", "0"));
    var stale = manager.Review();
    File.WriteAllText(autoApp("999"), Manifest("999", "1"));
    Reject<InvalidOperationException>(() => manager.ApplyConfirmed(stale, new HashSet<string> { "999" }), "Changed pending manifest invalidates confirmation");
    Check(File.ReadAllText(autoApp("999")) == Manifest("999", "1"), "Stale confirmation leaves manifest unchanged");
    config.Save(config.Load() with { Enabled = false });
    Check(manager.Tick().Result == null, "Disable stops pending automatic writes");
    Check(new ManagementSettingsStore(autoData).Load().Enabled == false, "Management settings persist");
    Reject<FormatException>(() => config.Save(config.Load() with { ApplyBehaviour = "invalid" }), "Invalid management settings rejected");
    Check(new ChangeHistory(autoData, () => false).List().All(b => b.Kind == "management"), "Automatic operations use recoverable history batches");

    // Controller uses a fake client; no real Steam process is launched or stopped.
    var simulated = new FakeSteamClient { State = SteamClientState.Idle };
    var controller = new SteamOperationController(simulated, _ => Task.CompletedTask, 3);
    int permissionCalls = 0, writeCalls = 0;
    ChangeResult FixtureApply() { writeCalls++; return new(1, []); }
    var denied = await controller.RunAsync(() => { permissionCalls++; return false; }, FixtureApply);
    Check(denied.Status == "declined" && permissionCalls == 1 && writeCalls == 0 && simulated.ShutdownCalls == 0, "No consent means no shutdown or writes");
    var allowed = await controller.RunAsync(() => { permissionCalls++; return true; }, FixtureApply);
    Check(allowed.Status == "applied" && allowed.Relaunched && permissionCalls == 2 && writeCalls == 1 && simulated.ShutdownCalls == 1 && simulated.LaunchCalls == 1, "Yes shuts down, applies and relaunches");
    var again = await controller.RunAsync(() => { permissionCalls++; return false; }, FixtureApply);
    Check(again.Status == "declined" && permissionCalls == 3 && simulated.ShutdownCalls == 1, "Consent never remembered");
    simulated.State = SteamClientState.Busy;
    Check((await controller.RunAsync(() => true, FixtureApply)).Status == "manual-required" && writeCalls == 1, "Running game blocks shutdown");
    simulated.State = SteamClientState.Unknown;
    Check((await controller.RunAsync(() => true, FixtureApply)).Status == "manual-required", "Unknown state blocks shutdown");
    simulated.State = SteamClientState.Closed;
    var closed = await controller.RunAsync(() => throw new Exception("Unexpected consent"), FixtureApply);
    Check(closed.Status == "applied" && !closed.Relaunched && writeCalls == 2 && simulated.LaunchCalls == 1, "Already closed applies without shutdown consent or relaunch");
    simulated.State = SteamClientState.Idle;
    simulated.ExitOnShutdown = false;
    var timeout = await controller.RunAsync(() => true, FixtureApply);
    Check(timeout.Status == "timeout" && writeCalls == 2 && simulated.LaunchCalls == 1, "Timeout never writes or relaunches");
    simulated.ExitOnShutdown = true;
    var changedState = await controller.RunAsync(() => { simulated.State = SteamClientState.Busy; return true; }, FixtureApply);
    Check(changedState.Status == "manual-required" && writeCalls == 2, "Recheck after consent blocks newly running game");
    simulated.State = SteamClientState.Idle;
    var failedWrite = await controller.RunAsync(() => true, () => throw new IOException("Fixture write error"));
    Check(failedWrite.Status == "apply-error" && failedWrite.Relaunched && simulated.LaunchCalls == 2, "Apply error reported and originally running client reopened");
    simulated.State = SteamClientState.Idle;
    var externalRestart = await controller.RunAsync(() => true, () => { simulated.State = SteamClientState.Idle; return new(0, ["Steam restarted"]); });
    Check(externalRestart.Status == "partial-or-error" && !externalRestart.Relaunched && simulated.LaunchCalls == 2, "External restart does not launch duplicate");
    simulated.State = SteamClientState.Idle;
    simulated.FailLaunch = true;
    var failedLaunch = await controller.RunAsync(() => true, FixtureApply);
    Check(failedLaunch.Status == "relaunch-error" && failedLaunch.Result?.Changed == 1 && !failedLaunch.Relaunched, "Launch failure preserves verified apply result");
    simulated.FailLaunch = false;
    simulated.State = SteamClientState.Closed;
    Check(controller.LaunchSteam().Status == "launched" && controller.LaunchSteam().Status == "already-running", "Standalone launch avoids duplicate");
    simulated.State = SteamClientState.Idle;
    var independent = await controller.RunAsync(() => { simulated.State = SteamClientState.Closed; return true; }, FixtureApply);
    Check(independent.Status == "applied" && !independent.Relaunched, "Independent user exit not automatically reopened");

    string runtimeData = Path.Combine(root, "runtime-data");
    var runtime = new RuntimeSettingsStore(runtimeData);
    Check(runtime.Load() is { BackgroundMode: false, StartWithWindows: false, StartMinimized: true }, "Runtime defaults off; startup minimized preference on");
    Check(!RuntimeSettingsStore.HideOnClose(runtime.Load(), false, true), "Default window closes normally");
    runtime.Save(new(BackgroundMode: true, StartWithWindows: true));
    Check(new RuntimeSettingsStore(runtimeData).Load().BackgroundMode, "Runtime settings persist");
    Check(RuntimeSettingsStore.HideOnClose(runtime.Load(), false, true) &&
        !RuntimeSettingsStore.HideOnClose(runtime.Load(), true, true) &&
        !RuntimeSettingsStore.HideOnClose(runtime.Load(), false, false), "Tray close policy honors exit and system shutdown");
    Check(RuntimeSettingsStore.HideOnStartup(runtime.Load(), true) && !RuntimeSettingsStore.HideOnStartup(runtime.Load(), false), "Only startup launch hides window");
    string startupExe = Path.Combine(root, "Space folder", "SteamReady.exe");
    Check(WindowsStartup.Command(startupExe) == "\"" + startupExe + "\" --startup", "Startup command safely quotes executable");
    Reject<ArgumentException>(() => WindowsStartup.Command("relative.exe"), "Reject relative startup path");

    string queueData = Path.Combine(root, "queue-data");
    var qSettings = new ManagementSettingsStore(queueData);
    bool queueSteam = true;
    var queue = new BackgroundController(queueData, () => queueSteam, () => SteamFiles.Scan(autoRoot));
    var qScan = SteamFiles.Scan(autoRoot);
    var qGame = qScan.Games.Single(g => g.Id == "123");
    Reject<InvalidOperationException>(() => queue.Enqueue(qScan, [qGame], "2"), "Manual mode does not queue explicit changes");
    qSettings.Save(new(ApplyBehaviour: "auto"));
    var batchQueued = queue.Enqueue(qScan, [qGame], "2");
    Check(queue.Tick().Status == "waiting-for-steam", "Explicit queue waits for normal Steam exit");
    Reject<InvalidOperationException>(() => queue.Enqueue(qScan, [qGame], "0"), "Existing queue not silently replaced");
    queueSteam = false;
    var resumedQueue = new BackgroundController(queueData, () => queueSteam, () => SteamFiles.Scan(autoRoot));
    Check(resumedQueue.Pending?.Id == batchQueued.Id, "Explicit batch survives restart");
    var appliedQueue = resumedQueue.Tick();
    Check(appliedQueue.Status == "queue-applied" && appliedQueue.Result?.Changed == 1 && resumedQueue.Pending == null, "Explicit queue applies even with automatic management disabled");
    Check(new PreferenceStore(queueData).Load().Games["123"].Exception, "Queued explicit selection preserved as exception");
    Check(new ChangeHistory(queueData, () => false).List().Single().Kind == "queued", "Queued operation backed up");
    qSettings.Save(new(ApplyBehaviour: "notify"));
    qScan = SteamFiles.Scan(autoRoot);
    qGame = qScan.Games.Single(g => g.Id == "123");
    var notifyBatch = queue.Enqueue(qScan, [qGame], "0");
    Check(queue.Tick().Status == "queue-ready" && File.ReadAllText(qGame.Path) == Manifest("123", "2"), "Notify queue never writes without confirmation");
    Check(queue.ApplyConfirmed(notifyBatch.Id).Status == "queue-applied", "Explicit notify confirmation applies");
    qSettings.Save(new(ApplyBehaviour: "auto"));
    qScan = SteamFiles.Scan(autoRoot);
    qGame = qScan.Games.Single(g => g.Id == "123");
    var staleBatch = queue.Enqueue(qScan, [qGame], "2");
    File.WriteAllText(qGame.Path, Manifest("123", "1"));
    Check(queue.Tick().Status == "queue-review" && queue.Pending?.NeedsReview == true && File.ReadAllText(qGame.Path) == Manifest("123", "1"), "Stale queued selection blocked without overwrite");
    Check(queue.Tick().Status == "queue-review", "Failed queue never blindly replays");
    queue.Cancel(staleBatch.Id);
    Check(queue.Pending == null, "Queue cancellation clears pending work");
    qScan = SteamFiles.Scan(autoRoot);
    qGame = qScan.Games.Single(g => g.Id == "123");
    var pausedBatch = queue.Enqueue(qScan, [qGame], "2");
    qSettings.Save(new(ApplyBehaviour: "manual"));
    Check(queue.Tick().Status == "queue-paused" && File.ReadAllText(qGame.Path) == Manifest("123", "1"), "Changing apply mode pauses queued work");
    queue.Cancel(pausedBatch.Id);

    var updates = new UpdateSettingsStore(Path.Combine(root, "update-data"));
    Check(updates.Load().AutomaticChecks && UpdateService.IsDue(updates.Load(), DateTimeOffset.UtcNow), "Automatic update checks enabled by default");
    var now = DateTimeOffset.UtcNow;
    updates.Save(new(LastAttemptUtc: now, SkippedVersion: "v1.2.3"));
    Check(!UpdateService.IsDue(updates.Load(), now.AddHours(23)) && UpdateService.IsDue(updates.Load(), now.AddHours(24)), "Automatic checks at most daily");
    Check(!UpdateService.IsDue(updates.Load() with { AutomaticChecks = false }, now.AddDays(2)), "Automatic checks can be disabled");
    string releaseJson = """
    {"tag_name":"v1.2.3","draft":false,"prerelease":false,"body":"Fixture notes","assets":[
    {"name":"SteamReady.exe","browser_download_url":"https://github.com/daemien6667/SteamReady/releases/download/v1.2.3/SteamReady.exe"},
    {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/daemien6667/SteamReady/releases/download/v1.2.3/SHA256SUMS.txt"}]}
    """;
    var release = UpdateService.ParseRelease(releaseJson, new Version(1, 2, 2));
    Check(release?.Version == new Version(1, 2, 3) && release.Notes == "Fixture notes", "Newer compatible official release detected");
    Check(UpdateService.ParseRelease(releaseJson, new Version(1, 2, 3)) == null, "Equal release not offered");
    Check(UpdateService.ParseRelease(releaseJson, new Version(2, 0, 0)) == null, "Downgrade not offered");
    Check(UpdateService.ParseRelease(releaseJson.Replace("\"prerelease\":false", "\"prerelease\":true"), new Version(1, 0, 0)) == null, "Prerelease ignored");
    Check(UpdateService.ParseRelease(releaseJson.Replace("\"draft\":false", "\"draft\":true"), new Version(1, 0, 0)) == null, "Draft ignored");
    Check(UpdateService.ParseRelease(releaseJson.Replace("\"name\":\"SHA256SUMS.txt\"", "\"name\":\"other.txt\""), new Version(1, 0, 0)) == null, "Missing checksum prevents install offer");
    Reject<FormatException>(() => UpdateService.ParseRelease(releaseJson.Replace("https://github.com/", "https://example.com/"), new Version(1, 0, 0)), "Untrusted release asset rejected");
    string binary = Path.Combine(root, "fake-update.exe");
    File.WriteAllBytes(binary, [77, 90, 1, 2, 3]);
    string digest = UpdateService.FileHash(binary);
    Check(UpdateService.ParseChecksum(digest + "  SteamReady.exe\n") == digest, "SHA256SUMS format accepted");
    Reject<FormatException>(() => UpdateService.ParseChecksum(digest + "  other.exe"), "Wrong checksum filename rejected");
    UpdateService.Verify(binary, digest);
    Check(true, "Verified fixture executable accepted");
    File.AppendAllText(binary, "changed");
    Reject<IOException>(() => UpdateService.Verify(binary, digest), "Corrupted executable rejected");
    Check(updates.Load().SkippedVersion == "v1.2.3", "Skipped version persisted");

}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine($"All {passed} fixture checks passed. No real Steam libraries accessed.");

internal sealed class FakeSteamClient : ISteamClient
{
    public SteamClientState State { get; set; }
    public int ShutdownCalls { get; private set; }
    public int LaunchCalls { get; private set; }
    public bool ExitOnShutdown { get; set; } = true;
    public bool FailLaunch { get; set; }
    public SteamClientState Probe() => State;
    public void RequestShutdown()
    {
        ShutdownCalls++;
        if (ExitOnShutdown) State = SteamClientState.Closed;
    }
    public void Launch()
    {
        if (FailLaunch) throw new IOException("Fixture launch error");
        LaunchCalls++;
        State = SteamClientState.Idle;
    }
}
