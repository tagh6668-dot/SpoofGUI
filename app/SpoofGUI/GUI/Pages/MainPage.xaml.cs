using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpoofGUI.Core;
using SpoofGUI.Engine;
using SpoofGUI.GUI.ViewModels;

namespace SpoofGUI.GUI.Pages;

public sealed partial class MainPage : Page, IMainPage
{
    private readonly MainPageViewModel _vm;

    public MainPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<MainPageViewModel>();
        Loaded += async (_, _) => await _vm.LoadAsync(this);
    }

    private void SetConnecting(bool on)
    {
        ConnectSpinner.IsActive = on;
        ConnectSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ConnectContent.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    public void RenderIdle(string profileName, string flow, string sni)
    {
        SetConnecting(false);
        HeadlineText.Text = "SpoofGUI";
        HeadlineSub.Text  = "Connect and use your X-Ray Client.";
        ProfileName.Text  = profileName;
        ProfileFlow.Text  = flow;
        ProfileSni.Text   = $"SNI: {sni}";
        StatUptime.Text = StatConns.Text = StatIface.Text = "—";
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
    }

    public void RenderV2RayCard(bool live, string mode, int socksPort, int httpPort, string lastError)
    {
        V2RayCardStatus.Text = live ? "live" : "idle";
        V2RayCardMode.Text = $"mode: {mode}";
        V2RayCardPorts.Text = $"socks 127.0.0.1:{socksPort} · http 127.0.0.1:{httpPort}";
        V2RayCardError.Text = lastError;
    }

    public void RenderConnecting()
    {
        SetConnecting(true);
        HeadlineText.Text = "starting";
        HeadlineSub.Text  = "Starting local listener and attaching WinDivert.";
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = false;
    }

    public void RenderLive(string iface, ulong uptimeMs, uint conns)
    {
        SetConnecting(false);
        HeadlineText.Text = "ready";
        HeadlineSub.Text  = $"Connect and use your X-Ray Client. {conns} active connection{(conns == 1 ? "" : "s")}.";
        StatUptime.Text = FormatUptime(uptimeMs);
        StatConns.Text  = conns.ToString();
        StatIface.Text  = iface;
        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
    }

    public void RenderError(string message)
    {
        SetConnecting(false);
        HeadlineText.Text = "error";
        HeadlineSub.Text  = message;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
    }

    private static string FormatUptime(ulong ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private async void OnConnect(object sender, object e)
    {
        if (!await EnsureWinDivertAsync()) return;
        await _vm.ConnectAsync();
    }

    private async Task<bool> EnsureWinDivertAsync()
    {
        if (WinDivert.IsAvailable()) return true;

        var askDownload = new ContentDialog
        {
            Title = "WinDivert not found",
            Content = $"SpoofGUI needs WinDivert.dll and {WinDivert.RequiredDriverName}. The installer no longer bundles WinDivert because some antivirus tools flag installer temp extraction. Download official WinDivert now?",
            PrimaryButtonText = "download",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };
        if (await askDownload.ShowAsync() != ContentDialogResult.Primary)
        {
            RenderError("WinDivert is required to start SNI spoofing.");
            return false;
        }

        var archBox = new ComboBox
        {
            MinWidth = 220,
            SelectedIndex = Environment.Is64BitOperatingSystem ? 0 : 1,
        };
        archBox.Items.Add(new ComboBoxItem { Content = "amd64" });
        archBox.Items.Add(new ComboBoxItem { Content = "x86" });

        var askArch = new ContentDialog
        {
            Title = "Choose desktop architecture",
            Content = archBox,
            PrimaryButtonText = "continue",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };
        if (await askArch.ShowAsync() != ContentDialogResult.Primary)
        {
            RenderError("WinDivert download cancelled.");
            return false;
        }

        var arch = ((ComboBoxItem)archBox.SelectedItem).Content?.ToString() ?? "amd64";
        var progress = new Progress<string>(message => HeadlineSub.Text = message);
        try
        {
            ConnectButton.IsEnabled = false;
            HeadlineText.Text = "preparing";
            await WinDivertDownloader.DownloadAsync(arch, progress);
            return true;
        }
        catch (Exception ex)
        {
            RenderError($"WinDivert download failed: {ex.Message}");
            return false;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }
    private async void OnDisconnect(object sender, object e) => await _vm.DisconnectAsync();
    private void OnEditProfile(object sender, object e) => Frame.Navigate(typeof(ConfigPage));
}
