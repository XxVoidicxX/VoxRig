using System.Windows;

namespace VoxRig;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            int code = SelfTest.Run(args[1], args.Length >= 3 && args[2] == "live");
            Shutdown(code);
            return;
        }
        string snapshot = args.Length >= 2 && args[0] == "--snapshot" ? args[1] : null;
        new MainWindow(snapshot).Show();
    }
}
