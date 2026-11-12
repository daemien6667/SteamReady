using System.Drawing;
namespace SteamReady;

// Development-only functional controls. The final visual design is deferred.
internal sealed class SettingsDialog : Form
{
    private readonly RuntimeSettingsStore runtime;
    private readonly ManagementSettingsStore management;
    private readonly CheckBox background = new() { Text = "Keep running in system tray", AutoSize = true };
    private readonly CheckBox startup = new() { Text = "Start with Windows", AutoSize = true };
    private readonly CheckBox minimized = new() { Text = "Start minimized", AutoSize = true };
    private readonly CheckBox automatic = new() { Text = "Automatically manage game preferences", AutoSize = true };
    private readonly CheckBox askManual = new() { Text = "Ask about changes made in Steam", AutoSize = true };
    private readonly CheckBox askReinstall = new() { Text = "Ask before restoring reinstalled games", AutoSize = true };
    private readonly CheckBox notifications = new() { Text = "Show management notifications", AutoSize = true };
    private readonly ComboBox scope = Choice("New games only", "Continuous management");
    private readonly ComboBox target = Choice("Immediately download updates", "Let Steam decide", "Only when launched");
    private readonly ComboBox behaviour = Choice("Manual (default)", "Automatically when Steam closes", "Notify when ready");
    private readonly NumericUpDown retention = new() { Minimum = 50, Maximum = int.MaxValue, Width = 110 };
    private readonly ChangeHistory history;
    private static ComboBox Choice(params string[] values)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
        c.Items.AddRange(values); return c;
    }
    public SettingsDialog(string data)
    {
        runtime = new(data); management = new(data); history = new(data, SteamFiles.SteamRunning);
        var r = runtime.Load(); var m = management.Load();
        Text = "SteamReady settings"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(490, 535); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        background.Checked = r.BackgroundMode; startup.Checked = r.StartWithWindows; minimized.Checked = r.StartMinimized;
        automatic.Checked = m.Enabled; scope.SelectedIndex = m.Scope == "continuous" ? 1 : 0;
        target.SelectedIndex = m.DefaultMode switch { "2" => 0, "0" => 1, _ => 2 };
        behaviour.SelectedIndex = m.ApplyBehaviour switch { "auto" => 1, "notify" => 2, _ => 0 };
        askManual.Checked = m.AskOnManualChange; askReinstall.Checked = m.AskOnReinstall; notifications.Checked = m.Notifications;
        retention.Value = history.Retention;
        var save = new Button { Text = "Save settings", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        panel.Controls.AddRange([background, startup, minimized, automatic,
            new Label { Text = "Automatic management scope", AutoSize = true }, scope,
            new Label { Text = "Default update preference", AutoSize = true }, target,
            new Label { Text = "Apply behaviour", AutoSize = true }, behaviour,
            askManual, askReinstall, notifications,
            new Label { Text = "Change batches to retain", AutoSize = true }, retention, save, cancel]);
        Controls.Add(panel); AcceptButton = save; CancelButton = cancel;
        save.Click += (_, _) =>
        {
            try
            {
                var next = new RuntimeSettings(BackgroundMode: background.Checked, StartWithWindows: startup.Checked, StartMinimized: minimized.Checked);
                var policy = new ManagementSettings(Enabled: automatic.Checked,
                    Scope: scope.SelectedIndex == 1 ? "continuous" : "new",
                    DefaultMode: target.SelectedIndex switch { 0 => "2", 1 => "0", _ => "1" },
                    ApplyBehaviour: behaviour.SelectedIndex switch { 1 => "auto", 2 => "notify", _ => "manual" },
                    AskOnManualChange: askManual.Checked, AskOnReinstall: askReinstall.Checked, Notifications: notifications.Checked);
                if (policy.Enabled && policy.ApplyBehaviour == "auto" &&
                    MessageBox.Show(this, "Allow SteamReady to automatically change update preferences when Steam is closed?", "Automatic changes", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                if (next.StartWithWindows != r.StartWithWindows)
                    WindowsStartup.SetEnabled(next.StartWithWindows, Environment.ProcessPath!);
                runtime.Save(next);
                management.Save(policy);
                if ((int)retention.Value != history.Retention) history.SetRetention((int)retention.Value);
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Settings not fully saved"); }
        };
    }
}

internal sealed class HistoryDialog : Form
{
    private readonly ListBox batches = new() { Dock = DockStyle.Fill };
    private readonly CheckedListBox games = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly List<HistoryBatch> records;
    public string? BatchId { get; private set; }
    public HashSet<string> SelectedIds { get; private set; } = [];
    public HistoryDialog(List<HistoryBatch> records)
    {
        this.records = records;
        Text = "Change history"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(750, 450);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 280 };
        split.Panel1.Controls.Add(batches); split.Panel2.Controls.Add(games);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var restore = new Button { Text = "Restore checked games", AutoSize = true };
        var all = new Button { Text = "Select all in batch", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([restore, all, cancel]); Controls.Add(split); Controls.Add(actions);
        foreach (var b in records) batches.Items.Add($"{b.CreatedUtc.ToLocalTime():g} | {b.Kind} | {b.Entries.Count} app(s)");
        batches.SelectedIndexChanged += (_, _) =>
        {
            games.Items.Clear();
            if (batches.SelectedIndex < 0) return;
            foreach (var e in records[batches.SelectedIndex].Entries.Where(e => e.State is "applied" or "pending" or "legacy"))
                games.Items.Add(new EntryChoice(e), true);
        };
        all.Click += (_, _) => { for (int i = 0; i < games.Items.Count; i++) games.SetItemChecked(i, true); };
        restore.Click += (_, _) =>
        {
            if (batches.SelectedIndex < 0 || games.CheckedItems.Count == 0) return;
            BatchId = records[batches.SelectedIndex].Id;
            SelectedIds = games.CheckedItems.Cast<EntryChoice>().Select(e => e.Entry.Id).ToHashSet();
            DialogResult = DialogResult.OK;
        };
        if (batches.Items.Count > 0) batches.SelectedIndex = 0;
    }
    private sealed record EntryChoice(HistoryEntry Entry)
    {
        public override string ToString() => $"{Entry.Name} [{Entry.Id}]  {Entry.Before} → {Entry.After} ({Entry.State})";
    }
}
