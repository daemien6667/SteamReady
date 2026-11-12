using System.Drawing;
namespace SteamReady;

// Functional shell only: existing window layout remains unchanged.
// WinForms timer runs on the UI thread; manual operations suspend polling.
internal sealed class RuntimeHost : ApplicationContext
{
    private readonly MainForm form;
    private readonly RuntimeSettingsStore settings;
    private readonly ManagementSettingsStore management;
    private readonly BackgroundController controller;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private readonly NotifyIcon tray;
    private bool explicitExit;
    private bool disposed;
    private bool tickActive;
    private string? lastNotice;
    public RuntimeHost(string dataRoot, bool startupLaunch)
    {
        settings = new(dataRoot);
        management = new(dataRoot);
        form = new MainForm();
        form.UpdateInstallReady += ExitApplication;
        form.RuntimeSettingsChanged += () => { tray.Visible = ReadSettings().BackgroundMode || !form.Visible; };
        controller = new(dataRoot, SteamFiles.SteamRunning, () =>
        {
            string root = form.SteamRoot ?? SteamFiles.FindSteam() ?? throw new InvalidOperationException("Steam not detected.");
            return SteamFiles.Scan(root);
        });
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SteamReady", null, (_, _) => OpenWindow());
        menu.Items.Add("Exit SteamReady", null, (_, _) => ExitApplication());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "SteamReady", ContextMenuStrip = menu };
        form.UpdateAvailable += version =>
        {
            if (tray.Visible) tray.ShowBalloonTip(5000, "SteamReady", "Version " + version + " is available. Open SteamReady to install.", ToolTipIcon.Info);
        };
        tray.DoubleClick += (_, _) => OpenWindow();
        tray.BalloonTipClicked += (_, _) => OpenWindow();
        form.FormClosing += (_, e) =>
        {
            if (form.OperationActive && !form.UpdateExitRequested && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (RuntimeSettingsStore.HideOnClose(ReadSettings(), explicitExit, e.CloseReason == CloseReason.UserClosing))
            {
                e.Cancel = true;
                form.Hide();
                tray.Visible = true;
            }
        };
        form.FormClosed += (_, _) => ExitThread();
        timer.Tick += async (_, _) => { Poll(); await form.CheckUpdatesAsync(false); };
        var initial = ReadSettings();
        bool hide = RuntimeSettingsStore.HideOnStartup(initial, startupLaunch);
        tray.Visible = initial.BackgroundMode || hide;
        if (!hide) form.Show();
        timer.Start();
    }
    private RuntimeSettings ReadSettings()
    {
        try { return settings.Load(); }
        catch { return new(); } // Invalid settings must not enable background/startup.
    }
    private void OpenWindow()
    {
        if (disposed) return;
        form.Show();
        form.WindowState = FormWindowState.Normal;
        form.Activate();
        if (!ReadSettings().BackgroundMode) tray.Visible = false;
    }
    private void ExitApplication()
    {
        if (form.OperationActive && !form.UpdateExitRequested) return;
        explicitExit = true;
        form.Close();
        // A hidden form that never created a window may not raise FormClosed.
        if (!disposed) ExitThread();
    }
    private void Poll()
    {
        if (disposed || tickActive || form.OperationActive) return;
        tickActive = true;
        try
        {
            var config = management.Load();
            var queued = controller.Pending;
            if (!config.Enabled && queued == null) return;
            var result = controller.Tick();
            // The current window never silently resets the user's checked selection.
            // Stale selections are rejected by the existing writer; refresh is manual.
            bool requiredNotice = result.Status is "queue-ready" or "queue-review" or "queue-paused" ||
                (result.Review is { SteamRunning: false } r && r.Candidates.Any(c => c.RequiresConfirmation || r.Settings.ApplyBehaviour == "notify"));
            if (!config.Notifications && !requiredNotice) return;
            string? notice = result.Status switch
            {
                "queue-ready" => "Queued changes are ready for confirmation.",
                "queue-review" => result.Detail ?? "Queued changes need review.",
                "queue-paused" => result.Detail ?? "Queued changes paused.",
                "queue-applied" => "Queued game preferences applied.",
                "managed" => $"{result.Result?.Changed ?? 0} game preference(s) updated.",
                "error" => result.Detail,
                _ when result.Review is { SteamRunning: false } review && review.Candidates.Count > 0
                    => $"{review.Candidates.Count} game preference change(s) await review.",
                _ => null
            };
            if (notice != null && notice != lastNotice)
            {
                form.ShowRuntimeNotice(notice);
                if (tray.Visible) tray.ShowBalloonTip(5000, "SteamReady", notice, ToolTipIcon.Info);
            }
            lastNotice = notice;
        }
        catch (Exception ex)
        {
            if (tray.Visible && lastNotice != ex.Message)
            {
                lastNotice = ex.Message;
                tray.ShowBalloonTip(5000, "SteamReady", ex.Message, ToolTipIcon.Warning);
            }
        }
        finally { tickActive = false; }
    }
    protected override void ExitThreadCore()
    {
        if (!disposed)
        {
            disposed = true;
            timer.Stop();
            timer.Dispose();
            tray.Visible = false;
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        base.ExitThreadCore();
    }
}
