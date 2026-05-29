using Microsoft.UI.Xaml;
using SpoofGUI.Core;

namespace SpoofGUI;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? CurrentWindow { get; private set; }
    private Window? _window;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("App.UnhandledException", e.Exception);
            e.Handled = true;
        };

        Application.LoadComponent(this, new Uri("ms-appx:///App.xaml"));
        Services = Bootstrap.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {

        CurrentWindow = new MainWindow();
        _window = CurrentWindow;
        _window.Activate();
    }
}
