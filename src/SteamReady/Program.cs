using System.Threading;
namespace SteamReady;
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\SteamReady", out bool first);
        if (!first) { MessageBox.Show("SteamReady is already running."); return; }
        ApplicationConfiguration.Initialize();
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamReady");
        using var host = new RuntimeHost(data, args.Contains("--startup", StringComparer.OrdinalIgnoreCase));
        Application.Run(host);
    }
}
