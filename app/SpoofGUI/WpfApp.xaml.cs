using System.Windows;
using SpoofGUI.Core;

namespace SpoofGUI;

public partial class WpfApp : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public WpfApp()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("WpfAppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("WpfUnobservedTask", e.Exception);
            e.SetObserved();
        };

        Services = Bootstrap.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var mainWin = new WpfMainWindow();
        mainWin.Show();
    }
}
