using System.Drawing;
namespace SteamReady;
internal sealed class MainForm : Form
{
    private readonly ListView list = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly Label status = new() { AutoSize = true };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button apply = new() { Text = "Apply to selected", AutoSize = true };
    private readonly Button undo = new() { Text = "Undo last batch", AutoSize = true };
    private readonly Button checkUpdates = new() { Text = "Check for updates", AutoSize = true };
    private readonly Button installUpdate = new() { Text = "Download & Install Update", AutoSize = true, Visible = false };
    private readonly Button skipUpdate = new() { Text = "Skip this version", AutoSize = true, Visible = false };
    private readonly CheckBox autoUpdates = new() { Text = "Check automatically", AutoSize = true };
    private readonly UpdateSettingsStore updateSettings = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamReady"));
    private readonly HttpClient updateHttp = new() { Timeout = TimeSpan.FromMinutes(2) };
    private SteamReadyRelease? availableUpdate;
    private bool checkingUpdates;
    private bool updateExitRequested;
    internal event Action? UpdateInstallReady;
    internal event Action<string>? UpdateAvailable;
    internal bool UpdateExitRequested => updateExitRequested;
    private static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamReady");
    private readonly ComboBox filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175 };
    private readonly HashSet<string> checkedPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool rebuilding;
    internal event Action? RuntimeSettingsChanged;
    private BackgroundController Queue => new(Data, SteamFiles.SteamRunning, FreshScan);
    private ScanResult FreshScan() => SteamFiles.Scan(root ?? throw new InvalidOperationException("Choose Steam folder first."));
    private ScanResult? scan;
    private string? root;
    internal string? SteamRoot => root;
    internal bool OperationActive { get; private set; }
    private void RunManual(Action action)
    {
        if (OperationActive) return;
        OperationActive = true;
        try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "SteamReady"); } finally { OperationActive = false; UpdateButtons(); }
    }
    public MainForm()
    {
        Text = "SteamReady | Development preview";
        MinimumSize = new Size(750, 450);
        Size = new Size(980, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(26, 35, 47);
        ForeColor = Color.WhiteSmoke;
        list.BackColor = Color.FromArgb(37, 49, 64);
        list.ForeColor = Color.WhiteSmoke;
        list.Columns.Add("Installed Steam app", 310);
        list.Columns.Add("Current preference", 205);
        list.Columns.Add("Library", 350);
        mode.Items.AddRange(new object[] { "Immediately download updates", "Follow Steam's default", "Only when launched" });
        mode.Items.Insert(0, "Mixed preferences / choose preference");
        mode.SelectedIndex = 0;
        filter.Items.AddRange(["All games", "Immediate", "Let Steam decide", "On launch"]);
        filter.SelectedIndex = 0;
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        var browse = new Button { Text = "Choose Steam folder", AutoSize = true };
        var rescan = new Button { Text = "Scan / refresh", AutoSize = true };
        var all = new Button { Text = "Select all", AutoSize = true };
        var none = new Button { Text = "Clear selection", AutoSize = true };
        var settings = new Button { Text = "Settings", AutoSize = true };
        var pending = new Button { Text = "Pending changes", AutoSize = true };
        var launch = new Button { Text = "Launch Steam", AutoSize = true };
        var history = new Button { Text = "History", AutoSize = true };
        var queue = new Button { Text = "Queue selected", AutoSize = true };
        var remembered = new Button { Text = "Saved games", AutoSize = true };
        top.Controls.AddRange(new Control[] { browse, rescan, all, none, filter, settings, pending, launch, history, remembered, checkUpdates, installUpdate, skipUpdate, autoUpdates });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        bottom.Controls.AddRange(new Control[] { mode, apply, undo, queue });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.Padding = new Padding(8);
        layout.Controls.Add(top, 0, 0); layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(status, 0, 2); layout.Controls.Add(bottom, 0, 3);
        Controls.Add(layout);
        foreach (var b in new[] { browse, rescan, all, none, apply, undo, checkUpdates, installUpdate, skipUpdate })
        {
            b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.FromArgb(58, 83, 109); b.ForeColor = Color.White;
        }
        browse.Click += (_, _) => RunManual(() => { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog() == DialogResult.OK) { root = picker.SelectedPath; RefreshList(); } });
        rescan.Click += (_, _) => RunManual(RefreshList);
        all.Click += (_, _) => { foreach (ListViewItem item in list.Items) item.Checked = true; };
        none.Click += (_, _) => { foreach (ListViewItem item in list.Items) item.Checked = false; };
        list.ItemChecked += (_, e) =>
        {
            if (rebuilding) return;
            if (e.Item.Tag is Game g) { if (e.Item.Checked) checkedPaths.Add(g.Path); else checkedPaths.Remove(g.Path); }
            UpdateSelectionMode();
        };
        filter.SelectedIndexChanged += (_, _) => RenderGames();
        mode.SelectedIndexChanged += (_, _) => UpdateButtons();
        apply.Click += async (_, _) => await RunOperation(Apply);
        undo.Click += async (_, _) => await RunOperation(Undo);
        history.Click += async (_, _) => await RunOperation(RestoreHistory);
        queue.Click += (_, _) => RunManual(QueueSelected);
        pending.Click += (_, _) => RunManual(ReviewPending);
        remembered.Click += (_, _) => RunManual(ReviewSaved);
        launch.Click += (_, _) => RunManual(() =>
        {
            var result = Client().LaunchSteam();
            MessageBox.Show(this, result.Detail ?? result.Status, "Launch Steam");
        });
        settings.Click += (_, _) => RunManual(() =>
        {
            using var dialog = new SettingsDialog(Data);
            if (dialog.ShowDialog(this) == DialogResult.OK) RuntimeSettingsChanged?.Invoke();
        });
        try { autoUpdates.Checked = updateSettings.Load().AutomaticChecks; } catch { autoUpdates.Checked = false; }
        autoUpdates.CheckedChanged += (_, _) =>
        {
            try { updateSettings.Save(updateSettings.Load() with { AutomaticChecks = autoUpdates.Checked }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Update settings"); }
        };
        checkUpdates.Click += async (_, _) => await CheckUpdatesAsync(true);
        installUpdate.Click += async (_, _) => await InstallUpdateAsync();
        skipUpdate.Click += (_, _) =>
        {
            if (availableUpdate == null) return;
            try
            {
                updateSettings.Save(updateSettings.Load() with { SkippedVersion = availableUpdate.Tag });
                SetUpdate(null);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Update settings"); }
        };
        FormClosing += (_, e) =>
        {
            if (OperationActive && !updateExitRequested && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        };
        FormClosed += (_, _) => updateHttp.Dispose();
        Shown += (_, _) => RunManual(RefreshList);
        UpdateButtons();
    }
    private void SetUpdate(SteamReadyRelease? release)
    {
        availableUpdate = release;
        installUpdate.Visible = skipUpdate.Visible = release != null;
        if (release != null) installUpdate.Text = "Download & Install " + release.Tag;
    }
    internal async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates || OperationActive || IsDisposed) return;
        checkingUpdates = true;
        try
        {
            var settings = updateSettings.Load();
            if (!manual && !UpdateService.IsDue(settings, DateTimeOffset.UtcNow)) return;
            // Save the attempt before networking so repeated failures never spam GitHub.
            updateSettings.Save(settings with { LastAttemptUtc = DateTimeOffset.UtcNow });
            var release = await new UpdateService(updateHttp).CheckAsync(UpdateService.CurrentVersion);
            if (IsDisposed) return;
            if (!manual && release?.Tag == settings.SkippedVersion) release = null;
            SetUpdate(release);
            if (release != null)
            {
                UpdateAvailable?.Invoke(release.Tag);
                if (manual) MessageBox.Show(this, "SteamReady " + release.Tag + " is available.\n\n" + release.Notes, "Release notes");
            }
            else if (manual) MessageBox.Show(this, "No newer installable release is available.", "SteamReady updates");
        }
        catch (Exception ex) { if (manual && !IsDisposed) MessageBox.Show(this, ex.Message, "Update check failed"); }
        finally { checkingUpdates = false; }
    }
    private async Task InstallUpdateAsync()
    {
        if (OperationActive || checkingUpdates || availableUpdate == null) return;
        var release = availableUpdate;
        if (MessageBox.Show(this, "Install SteamReady " + release.Tag + "? SteamReady will close and reopen. Your settings and Steam games will not be changed.\n\n" + release.Notes,
            "Install update", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        OperationActive = true;
        installUpdate.Enabled = false;
        checkUpdates.Enabled = false;
        skipUpdate.Enabled = false;
        try
        {
            status.Text = "Downloading and verifying SteamReady " + release.Tag + "...";
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await new UpdateService(updateHttp).StageAndStartAsync(release, Environment.ProcessPath!, timeout.Token);
            updateExitRequested = true;
            UpdateInstallReady?.Invoke();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update installation failed"); }
        finally
        {
            OperationActive = false;
            if (!IsDisposed) { installUpdate.Enabled = checkUpdates.Enabled = skipUpdate.Enabled = true; if (!updateExitRequested) RefreshList(); }
        }
    }
    private static string Display(string value) => value switch { "2" => "Immediate", "1" => "On launch", _ => "Steam default" };
    private string? Target => mode.SelectedIndex switch { 1 => "2", 2 => "0", 3 => "1", _ => null };
    private Game[] Selected() => list.CheckedItems.Cast<ListViewItem>().Select(i => (Game)i.Tag!).ToArray();
    private void UpdateButtons()
    {
        apply.Enabled = !OperationActive && scan != null && Selected().Length > 0 && Target != null;
        undo.Enabled = !OperationActive && scan != null && SteamFiles.HasUndo;
    }
    private void UpdateSelectionMode()
    {
        var modes = Selected().Select(g => g.Mode).Distinct().ToArray();
        mode.SelectedIndex = modes.Length == 1 ? modes[0] switch { "2" => 1, "0" => 2, _ => 3 } : 0;
        UpdateButtons();
    }
    private void RenderGames()
    {
        rebuilding = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            if (scan == null) return;
            string? match = filter.SelectedIndex switch { 1 => "2", 2 => "0", 3 => "1", _ => null };
            foreach (var game in scan.Games.Where(g => match == null || g.Mode == match))
            {
                var item = new ListViewItem(game.Name) { Tag = game };
                item.SubItems.Add(Display(game.Mode)); item.SubItems.Add(game.Library);
                list.Items.Add(item); item.Checked = checkedPaths.Contains(game.Path);
            }
        }
        finally { list.EndUpdate(); rebuilding = false; UpdateSelectionMode(); }
    }
    private void RefreshList()
    {
        try
        {
            root ??= SteamFiles.FindSteam();
            if (root == null) { status.Text = "Steam not detected. Choose its installation folder."; return; }
            var previous = scan;
            scan = SteamFiles.Scan(root);
            if (previous == null || previous.Root != scan.Root)
            {
                checkedPaths.Clear();
                foreach (var g in scan.Games) checkedPaths.Add(g.Path);
            }
            else
            {
                checkedPaths.IntersectWith(scan.Games.Select(g => g.Path));
            }
            // Track a baseline even when automatic writes are disabled.
            // Do not change saved preferences on incomplete scans.
            var config = new ManagementSettingsStore(Data).Load();
            new PreferenceStore(Data).Observe(scan, config.AskOnManualChange, config.AskOnReinstall);
            RenderGames();
            status.Text = $"{scan.Games.Count} apps | {scan.Libraries.Count} libraries | {scan.Games.Count(g => g.Mode == "2")} immediate";
            if (scan.Warnings.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, scan.Warnings), "Some entries could not be scanned");
        }
        catch (Exception ex) { scan = null; RenderGames(); status.Text = ex.Message; }
        finally { UpdateButtons(); }
    }
    private SteamOperationController Client()
    {
        var fresh = FreshScan();
        return new(new WindowsSteamClient(fresh.Root, fresh.Libraries));
    }
    private async Task RunOperation(Func<Task> action)
    {
        if (OperationActive) return;
        OperationActive = true; UpdateButtons();
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "SteamReady"); }
        finally { OperationActive = false; if (!IsDisposed) UpdateButtons(); }
    }
    private bool Consent() => MessageBox.Show(this,
        "Steam is running. Close all games first. May SteamReady close Steam normally, apply and verify your changes, then reopen Steam?",
        "Close Steam?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    private async Task Apply()
    {
        var target = Target;
        if (target == null) return;
        var selected = Selected().Where(g => g.Mode != target).ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "Selected apps already have this preference."); return; }
        if (Queue.Pending != null) { MessageBox.Show(this, "Resolve or cancel the pending batch first."); return; }
        if (MessageBox.Show(this, $"Change {selected.Length} app(s) to {Display(target)}? Original manifests will be backed up.",
            "Confirm changes", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var result = await Client().RunAsync(Consent, () => SteamFiles.Apply(selected, target));
        ShowOperation(result); RefreshList();
    }
    private async Task Undo()
    {
        if (scan == null) return;
        if (Queue.Pending != null) { MessageBox.Show(this, "Resolve or cancel the pending batch first."); return; }
        if (MessageBox.Show(this, "Restore previous preferences from the latest batch?", "Confirm undo", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        var result = await Client().RunAsync(Consent, () => SteamFiles.Undo(FreshScan()));
        ShowOperation(result); RefreshList();
    }
    private async Task RestoreHistory()
    {
        if (Queue.Pending != null) { MessageBox.Show(this, "Resolve or cancel the pending batch first."); return; }
        using var dialog = new HistoryDialog(new ChangeHistory(Data, SteamFiles.SteamRunning).List());
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.BatchId == null) return;
        var result = await Client().RunAsync(Consent, () => SteamFiles.Restore(FreshScan(), dialog.BatchId, dialog.SelectedIds));
        ShowOperation(result); RefreshList();
    }
    private void QueueSelected()
    {
        try
        {
            if (scan == null || Target == null) return;
            if (MessageBox.Show(this, "Queue these selected changes for when Steam closes?", "Queue changes", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Queue.Enqueue(scan, Selected(), Target);
            MessageBox.Show(this, "Changes queued. Keep SteamReady running to process them.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Queue"); }
    }
    private void ReviewPending()
    {
        try
        {
            var pending = Queue.Pending;
            if (pending != null)
            {
                string description = string.Join("\n", pending.Games.Select(g => $"{g.Id}: {Display(g.Expected)} → {Display(g.Desired)}"));
                var choice = MessageBox.Show(this, description + "\n\nYes = apply now (Steam must be closed). No = cancel batch. Cancel = leave queued.",
                    pending.NeedsReview ? "Pending batch needs review" : "Pending batch", MessageBoxButtons.YesNoCancel);
                if (choice == DialogResult.Yes)
                {
                    var result = Queue.ApplyConfirmed(pending.Id);
                    if (result.Result != null) ShowResult(result.Result);
                    if (result.Detail != null || result.Result == null) MessageBox.Show(this, result.Detail ?? result.Status);
                }
                else if (choice == DialogResult.No) Queue.Cancel(pending.Id);
            }
            else
            {
                var manager = new AutomaticManager(Data, SteamFiles.SteamRunning, FreshScan);
                var review = manager.Review();
                if (review.Candidates.Count == 0) { MessageBox.Show(this, "No pending management changes."); return; }
                foreach (var candidate in review.Candidates)
                {
                    var choice = MessageBox.Show(this, $"{candidate.Game.Name}: {Display(candidate.Game.Mode)} → {Display(candidate.Desired)}\n\nYes = apply (Steam closed). No = keep current as exception. Cancel = leave pending.",
                        "Review preference", MessageBoxButtons.YesNoCancel);
                    if (choice == DialogResult.Yes) ShowResult(manager.ApplyConfirmed(review, new HashSet<string> { candidate.Game.Id }));
                    else if (choice == DialogResult.No) manager.KeepCurrent(candidate);
                    else break;
                }
            }
            RefreshList();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Pending changes"); }
    }
    private void ReviewSaved()
    {
        try
        {
            var store = new PreferenceStore(Data);
            using var dialog = new Form { Text = "Saved games", Width = 620, Height = 440, StartPosition = FormStartPosition.CenterParent };
            var items = new ListBox { Dock = DockStyle.Fill };
            var records = store.Load().Games.Values.OrderBy(g => g.Name).ToList();
            foreach (var g in records) items.Items.Add($"{g.Name} [{g.Id}] | {Display(g.Preferred)} | {(g.Installed ? "Installed" : "Not installed")}{(g.Exception ? " | Exception" : "")}");
            var forget = new Button { Text = "Forget uninstalled game", Dock = DockStyle.Bottom, Height = 35 };
            forget.Click += (_, _) =>
            {
                if (items.SelectedIndex < 0) return;
                try
                {
                    var g = records[items.SelectedIndex];
                    if (MessageBox.Show(dialog, "Forget " + g.Name + "?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    store.Forget(g.Id); records.RemoveAt(items.SelectedIndex); items.Items.RemoveAt(items.SelectedIndex);
                }
                catch (Exception ex) { MessageBox.Show(dialog, ex.Message); }
            };
            dialog.Controls.Add(items); dialog.Controls.Add(forget); dialog.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Saved games"); }
    }
    private void ShowOperation(SteamOperation result)
    {
        if (result.Status == "declined") return;
        if (result.Result != null) ShowResult(result.Result);
        if (result.Detail != null || result.Result == null)
            MessageBox.Show(this, result.Detail ?? result.Status, "SteamReady");
    }
    private static void ShowResult(ChangeResult result) => MessageBox.Show(
        $"{result.Changed} preference(s) changed.\n" + (result.Errors.Count == 0 ? "No errors reported." : string.Join(Environment.NewLine, result.Errors)),
        "SteamReady result");
}
