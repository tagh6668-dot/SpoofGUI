using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using SpoofGUI.Core;
using SpoofGUI.GUI.Pages;
using SpoofGUI.GUI.ViewModels;

namespace SpoofGUI.GUI;

public sealed partial class Shell : UserControl
{
    public Shell()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Navigate(typeof(MainPage));
            await MaybeCheckUpdatesAsync();
        };
    }

    private void Navigate(Type page) => ContentFrame.Navigate(page);

    private void OnNavMain(object sender, object e)     => Navigate(typeof(MainPage));
    private void OnNavConfig(object sender, object e)   => Navigate(typeof(ConfigPage));
    private void OnNavV2Ray(object sender, object e)    => Navigate(typeof(V2RayPage));
    private void OnNavScanner(object sender, object e)  => Navigate(typeof(SniScannerPage));
    private void OnNavConnections(object sender, object e) => Navigate(typeof(ConnectionsPage));
    private void OnNavSettings(object sender, object e) => Navigate(typeof(SettingsPage));
    private void OnNavLogs(object sender, object e)     => Navigate(typeof(LogsPage));

    public void SetStatus(string line) { }

    private static async Task MaybeCheckUpdatesAsync()
    {
        try
        {
            var settings = App.Services.GetRequiredService<AppSettings>();
            if (!settings.CheckUpdatesOnLaunch) return;
            var vm = App.Services.GetRequiredService<SettingsPageViewModel>();
            var result = await vm.CheckForUpdatesAsync();
            AppLog.Info($"startup update check: {result.StatusText}");
        }
        catch (Exception e)
        {
            AppLog.Warn($"startup update check failed: {e.Message}");
        }
    }
}
