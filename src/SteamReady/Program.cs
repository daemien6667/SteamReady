using System.Threading;
namespace SteamReady;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--install-update")
        {
            try { UpdateInstaller.Install(Environment.ProcessPath!, args[1], args[2], args[3]); }
            catch (Exception ex) { MessageBox.Show("SteamReady update failed: " + ex.Message + "\nThe previous executable may be available in the update folder.", "SteamReady update"); }
            return;
        }
        using var mutex = new Mutex(true, @"Local\SteamReady", out bool first);
        if (!first) { MessageBox.Show("SteamReady is already running."); return; }
        ApplicationConfiguration.Initialize();
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamReady");
        using var host = new RuntimeHost(data, args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
        Application.Run(host);
    }
}
