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
    private ScanResult? scan;
    private string? root;
    internal string? SteamRoot => root;
    internal bool OperationActive { get; private set; }
    private void RunManual(Action action)
    {
        if (OperationActive) return;
        OperationActive = true;
        try { action(); } finally { OperationActive = false; }
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
        mode.SelectedIndex = 0;
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        var browse = new Button { Text = "Choose Steam folder", AutoSize = true };
        var rescan = new Button { Text = "Scan / refresh", AutoSize = true };
        var all = new Button { Text = "Select all", AutoSize = true };
        var none = new Button { Text = "Clear selection", AutoSize = true };
        top.Controls.AddRange(new Control[] { browse, rescan, all, none, checkUpdates, installUpdate, skipUpdate, autoUpdates });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        bottom.Controls.AddRange(new Control[] { mode, apply, undo });
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
        list.ItemChecked += (_, _) => UpdateButtons();
        apply.Click += (_, _) => RunManual(Apply);
        undo.Click += (_, _) => RunManual(Undo);
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
    private string Target => mode.SelectedIndex switch { 0 => "2", 1 => "0", _ => "1" };
    private void UpdateButtons() { apply.Enabled = scan != null && list.CheckedItems.Count > 0; undo.Enabled = scan != null && SteamFiles.HasUndo; }
    private void RefreshList()
    {
        scan = null; list.Items.Clear();
        try
        {
            root ??= SteamFiles.FindSteam();
            if (root == null) { status.Text = "Steam not detected. Choose its installation folder."; return; }
            scan = SteamFiles.Scan(root);
            foreach (var game in scan.Games)
            {
                var item = new ListViewItem(game.Name) { Tag = game };
                item.SubItems.Add(Display(game.Mode)); item.SubItems.Add(game.Library);
                list.Items.Add(item); item.Checked = true;
            }
            status.Text = $"{scan.Games.Count} apps | {scan.Libraries.Count} libraries | {scan.Games.Count(g => g.Mode == "2")} immediate";
            if (scan.Warnings.Count > 0) MessageBox.Show(string.Join(Environment.NewLine, scan.Warnings), "Some entries could not be scanned");
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { UpdateButtons(); }
    }
    private void Apply()
    {
        var selected = list.CheckedItems.Cast<ListViewItem>().Select(i => (Game)i.Tag!).Where(g => g.Mode != Target).ToArray();
        if (selected.Length == 0) { MessageBox.Show("Selected apps already have this preference."); return; }
        if (SteamFiles.SteamRunning()) { MessageBox.Show("Exit Steam normally (Steam > Exit), then click Apply again."); return; }
        if (MessageBox.Show($"Change {selected.Length} app(s) to {Display(Target)}?\n\nOriginal manifests will be backed up. Undo tracks this batch only; older backups remain on disk.", "Confirm changes", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        try { ShowResult(SteamFiles.Apply(selected, Target)); } catch (Exception ex) { MessageBox.Show(ex.Message); }
        RefreshList();
    }
    private void Undo()
    {
        if (scan == null) return;
        if (MessageBox.Show("Restore the previous preferences from the last batch? Steam must be closed.", "Confirm undo", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
        try { ShowResult(SteamFiles.Undo(scan)); } catch (Exception ex) { MessageBox.Show(ex.Message); }
        RefreshList();
    }
    private static void ShowResult(ChangeResult result) => MessageBox.Show(
        $"{result.Changed} preference(s) changed.\n" + (result.Errors.Count == 0 ? "No errors reported. Reopen Steam when finished." : string.Join(Environment.NewLine, result.Errors)),
        "SteamReady result");
}
