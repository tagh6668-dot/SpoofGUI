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
            await MaybeOfferCrashRecoveryAsync();
            await MaybeOfferProxyCleanupAsync();
            await MaybeCheckUpdatesAsync();
        };
    }

    private void Navigate(Type page) => ContentFrame.Navigate(page);

    private void OnNavMain(object sender, object e)     => Navigate(typeof(MainPage));
    private void OnNavConfig(object sender, object e)   => Navigate(typeof(ConfigPage));
    private void OnNavV2Ray(object sender, object e)    => Navigate(typeof(V2RayPage));
    private void OnNavRouting(object sender, object e)  => Navigate(typeof(RoutingPage));
    private void OnNavScanner(object sender, object e)  => Navigate(typeof(SniScannerPage));
    private void OnNavConnections(object sender, object e) => Navigate(typeof(ConnectionsPage));
    private void OnNavSettings(object sender, object e) => Navigate(typeof(SettingsPage));
    private void OnNavLogs(object sender, object e)     => Navigate(typeof(LogsPage));

    public void SetStatus(string line) { }

    private async Task MaybeOfferCrashRecoveryAsync()
    {
        try
        {
            var repo = App.Services.GetRequiredService<Database.SettingsRepository>();
            var wasRunning = repo.Get("app_running") == "1";
            repo.Set("app_running", "1");
            if (!wasRunning) return;

            var dialog = new ContentDialog
            {
                Title = "Previous session ended unexpectedly",
                Content = "Run crash recovery now? This disables stale proxy settings, clears kill switch rules, and stops leftover cores.",
                PrimaryButtonText = "recover",
                CloseButtonText = "skip",
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try { App.Services.GetRequiredService<ConnectionGuard>().Dispose(); } catch { }
            try { App.Services.GetRequiredService<SpoofGUI.Engine.SingBoxTunnelService>().Stop(); } catch { }
            try { await App.Services.GetRequiredService<SpoofGUI.Engine.XrayCoreService>().StopAsync(); } catch { }
            try { App.Services.GetRequiredService<SpoofGUI.Engine.EngineSupervisor>().Stop(); } catch { }
            try { if (SystemProxy.IsEnabled()) SystemProxy.Disable(); } catch { }
            AppLog.Info("crash recovery complete");
        }
        catch (Exception e)
        {
            AppLog.Warn($"crash recovery failed: {e.Message}");
        }
    }

    private async Task MaybeOfferProxyCleanupAsync()
    {
        try
        {
            var ports = App.Services.GetRequiredService<ProxyPortSettings>();
            var xray = App.Services.GetRequiredService<SpoofGUI.Engine.XrayCoreService>();
            var tunnel = App.Services.GetRequiredService<SpoofGUI.Engine.SingBoxTunnelService>();
            if (xray.IsRunning || tunnel.IsRunning || !SystemProxy.LooksLikeSpoofGuiProxy(ports)) return;

            var dialog = new ContentDialog
            {
                Title = "System proxy still points to SpoofGUI",
                Content = "Windows proxy appears to be left over from a previous run. Disable it now?",
                PrimaryButtonText = "disable proxy",
                CloseButtonText = "keep",
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                SystemProxy.Disable();
                AppLog.Info("startup proxy cleanup: disabled stale SpoofGUI proxy");
            }
        }
        catch (Exception e)
        {
            AppLog.Warn($"startup proxy cleanup failed: {e.Message}");
        }
    }

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
