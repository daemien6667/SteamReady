using System.Threading;
namespace SteamReady;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\SteamReady", out bool first);
        if (!first) { MessageBox.Show("SteamReady is already running."); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
