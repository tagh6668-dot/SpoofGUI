using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpoofGUI.Core;
using SpoofGUI.GUI.ViewModels;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SpoofGUI.GUI.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly SettingsPageViewModel _vm;
    private bool _initializing = true;

    public SettingsPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<SettingsPageViewModel>();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _initializing = true;
        UpdateVersion.Text = $"installed: {_vm.AppVersion}";
        UpdateLastCheck.Text = _vm.LastUpdateCheckText();
        ThemeChoice.SelectedIndex = _vm.Theme == "light" ? 1 : 0;
        SocksPortBox.Text = _vm.SocksPort.ToString();
        HttpPortBox.Text = _vm.HttpPort.ToString();
        AllowInsecureToggle.IsOn = _vm.XrayAllowInsecure;
        CheckOnLaunchToggle.IsOn = _vm.CheckUpdatesOnLaunch;
        FastModeToggle.IsOn = _vm.FastMode;
        KillSwitchToggle.IsOn = _vm.KillSwitch;
        LogLevelCombo.SelectedIndex = LogLevelToIndex(_vm.XrayLogLevel);
        DefaultModeCombo.SelectedIndex = ModeToIndex(_vm.V2RayMode);
        RemoteDnsBox.Text = _vm.RemoteDns;
        DirectDnsBox.Text = _vm.DirectDns;
        BootstrapDnsBox.Text = _vm.BootstrapDns;
        DnsStrategyCombo.SelectedIndex = DnsStrategyToIndex(_vm.DnsStrategy);
        DataFolderText.Text = _vm.DataFolder;
        _initializing = false;
    }

    private static int DnsStrategyToIndex(string strategy) => strategy switch
    {
        "prefer_ipv6" => 1,
        "ipv4_only" => 2,
        "ipv6_only" => 3,
        _ => 0,
    };

    private static string IndexToDnsStrategy(int index) => index switch
    {
        1 => "prefer_ipv6",
        2 => "ipv4_only",
        3 => "ipv6_only",
        _ => "prefer_ipv4",
    };

    private void OnSaveDns(object sender, object e)
    {
        _vm.SaveDns(RemoteDnsBox.Text, DirectDnsBox.Text, BootstrapDnsBox.Text);
        RemoteDnsBox.Text = _vm.RemoteDns;
        DirectDnsBox.Text = _vm.DirectDns;
        BootstrapDnsBox.Text = _vm.BootstrapDns;
        DnsStatus.Text = "saved (reconnect to apply)";
    }

    private void OnResetDns(object sender, object e)
    {
        _vm.SaveDns(AppSettings.DefaultRemoteDns, AppSettings.DefaultDirectDns, AppSettings.DefaultBootstrapDns);
        _vm.DnsStrategy = AppSettings.DefaultDnsStrategy;
        RemoteDnsBox.Text = _vm.RemoteDns;
        DirectDnsBox.Text = _vm.DirectDns;
        BootstrapDnsBox.Text = _vm.BootstrapDns;
        DnsStrategyCombo.SelectedIndex = DnsStrategyToIndex(_vm.DnsStrategy);
        DnsStatus.Text = "reset to defaults (reconnect to apply)";
    }

    private void OnDnsStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _vm.DnsStrategy = IndexToDnsStrategy(DnsStrategyCombo.SelectedIndex);
    }

    private static int LogLevelToIndex(string level) => level switch
    {
        "none" => 0,
        "error" => 1,
        "info" => 3,
        "debug" => 4,
        _ => 2,
    };

    private static string IndexToLogLevel(int index) => index switch
    {
        0 => "none",
        1 => "error",
        3 => "info",
        4 => "debug",
        _ => "warning",
    };

    private static int ModeToIndex(string mode) => mode switch
    {
        "Tunnel" => 1,
        "SystemProxy" => 2,
        _ => 0,
    };

    private static string IndexToMode(int index) => index switch
    {
        1 => "Tunnel",
        2 => "SystemProxy",
        _ => "Proxy",
    };

    private void OnSavePorts(object sender, object e)
    {
        var error = _vm.SavePorts(SocksPortBox.Text, HttpPortBox.Text);
        if (error is null)
        {
            PortsStatus.Text = $"saved: socks {_vm.SocksPort}, http {_vm.HttpPort} (reconnect to apply)";
            SocksPortBox.Text = _vm.SocksPort.ToString();
            HttpPortBox.Text = _vm.HttpPort.ToString();
        }
        else
        {
            PortsStatus.Text = $"not saved: {error}";
        }
    }

    private void OnResetPorts(object sender, object e)
    {
        PortsStatus.Text = _vm.ResetPorts() + " (reconnect to apply)";
        SocksPortBox.Text = _vm.SocksPort.ToString();
        HttpPortBox.Text = _vm.HttpPort.ToString();
    }

    private void OnAllowInsecureToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _vm.XrayAllowInsecure = AllowInsecureToggle.IsOn;
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _vm.XrayLogLevel = IndexToLogLevel(LogLevelCombo.SelectedIndex);
    }

    private void OnDefaultModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _vm.V2RayMode = IndexToMode(DefaultModeCombo.SelectedIndex);
    }

    private void OnCheckOnLaunchToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _vm.CheckUpdatesOnLaunch = CheckOnLaunchToggle.IsOn;
    }

    private void OnFastModeToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _vm.FastMode = FastModeToggle.IsOn;
    }

    private void OnOpenDataFolder(object sender, object e)
    {
        try { _vm.OpenDataFolder(); }
        catch { }
    }

    private async void OnCheckUpdates(object sender, object e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateLastCheck.Text = "checking...";
        UpdateReleaseLink.Visibility = Visibility.Collapsed;

        var res = await _vm.CheckForUpdatesAsync();
        UpdateVersion.Text = res.StatusText;
        UpdateLastCheck.Text = res.LastCheckText;
        UpdateReleaseLink.NavigateUri = new Uri(res.ReleaseUrl);
        UpdateReleaseLink.Content = res.IsUpdateAvailable ? "open new release" : "open latest release";
        UpdateReleaseLink.Visibility = Visibility.Visible;
        InstallUpdateButton.Visibility = res.IsUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = true;
    }

    private void OnKillSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _vm.KillSwitch = KillSwitchToggle.IsOn;
    }

    private async void OnInstallUpdate(object sender, object e)
    {
        InstallUpdateButton.IsEnabled = false;
        InstallUpdateLabel.Text = "downloading…";
        var res = await _vm.DownloadAndInstallLatestAsync();
        if (res == "INSTALL_LAUNCHED")
        {
            UpdateLastCheck.Text = "installer launched — closing SpoofGUI…";
            App.CurrentWindow?.QuitApp();
            return;
        }
        UpdateLastCheck.Text = res;
        InstallUpdateLabel.Text = "download & install";
        InstallUpdateButton.IsEnabled = true;
    }

    private async void OnExportProfiles(object sender, object e)
    {
        try
        {
            var picker = new FileSavePicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
            picker.SuggestedFileName = "spoofgui-backup";
            picker.FileTypeChoices.Add("JSON backup", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) { BackupStatus.Text = "export cancelled"; return; }
            var json = App.Services.GetRequiredService<ProfileBackupService>().ExportJson();
            await FileIO.WriteTextAsync(file, json);
            BackupStatus.Text = $"exported to {file.Name}";
        }
        catch (Exception ex) { BackupStatus.Text = $"export failed: {ex.Message}"; }
    }

    private async void OnImportProfiles(object sender, object e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow));
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null) { BackupStatus.Text = "import cancelled"; return; }
            var json = await FileIO.ReadTextAsync(file);
            var (spoof, v2ray) = App.Services.GetRequiredService<ProfileBackupService>().ImportJson(json);
            BackupStatus.Text = $"imported {spoof} SNI profile(s), {v2ray} V2Ray config(s)";
        }
        catch (Exception ex) { BackupStatus.Text = $"import failed: {ex.Message}"; }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var theme = ThemeChoice.SelectedIndex == 1 ? "light" : "dark";
        _vm.SetTheme(theme);
        App.CurrentWindow?.ApplyTheme(theme);
    }
}
