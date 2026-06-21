using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Engine;
using SpoofGUI.GUI.ViewModels;
using SpoofGUI.Models;

namespace SpoofGUI;

public partial class WpfMainWindow : Window, IMainPage
{
    private readonly MainPageViewModel _mainVm;
    private readonly ConfigPageViewModel _configVm;
    private readonly V2RayPageViewModel _v2rayVm;
    private readonly RoutingRuleRepository _routingRepo;
    private readonly SettingsRepository _settingsRepo;
    private readonly SettingsPageViewModel _settingsVm;
    private readonly SniScannerPageViewModel _sniScannerVm;
    private readonly EngineSupervisor _engineSupervisor;
    private readonly EngineClient _engineClient;

    private readonly DispatcherTimer _uiTimer;
    private string _currentOutboundIface = "—";

    public WpfMainWindow()
    {
        InitializeComponent();

        _mainVm = WpfApp.Services.GetRequiredService<MainPageViewModel>();
        _configVm = WpfApp.Services.GetRequiredService<ConfigPageViewModel>();
        _v2rayVm = WpfApp.Services.GetRequiredService<V2RayPageViewModel>();
        _routingRepo = WpfApp.Services.GetRequiredService<RoutingRuleRepository>();
        _settingsRepo = WpfApp.Services.GetRequiredService<SettingsRepository>();
        _settingsVm = WpfApp.Services.GetRequiredService<SettingsPageViewModel>();
        _sniScannerVm = WpfApp.Services.GetRequiredService<SniScannerPageViewModel>();
        _engineSupervisor = WpfApp.Services.GetRequiredService<EngineSupervisor>();
        _engineClient = WpfApp.Services.GetRequiredService<EngineClient>();

        _settingsRepo.Set("app_running", "1");

        // Load Initial Data
        Loaded += async (s, e) =>
        {
            await LoadDashboardAsync();
            LoadProfiles();
            LoadV2RayTab();
            LoadRoutingRules();
            LoadSettings();
            AppLog.Info("WPF GUI loaded successfully.");
        };

        // UI Refresh Timer (Logs, Connections, Stats)
        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _uiTimer.Stop();

        try { WpfApp.Services.GetRequiredService<ConnectionGuard>().Dispose(); } catch { }
        try { WpfApp.Services.GetRequiredService<SingBoxTunnelService>().Stop(); } catch { }
        try { WpfApp.Services.GetRequiredService<XrayCoreService>().Dispose(); } catch { }
        try { _engineSupervisor.Stop(); } catch { }
        try { _settingsRepo.Set("app_running", "0"); } catch { }
        try
        {
            var endpoint = $"http=127.0.0.1:{_settingsVm.HttpPort};https=127.0.0.1:{_settingsVm.HttpPort};socks=127.0.0.1:{_settingsVm.SocksPort}";
            if (SystemProxy.IsOurs(endpoint)) SystemProxy.Disable();
        }
        catch { }
    }

    private async void UiTimer_Tick(object? sender, EventArgs e)
    {
        // 1. Logs
        UpdateLogsView();

        // 2. Dashboard Realtime Check
        if (MainContentTabs.SelectedIndex == 0)
        {
            try
            {
                var status = await _engineClient.StatusAsync();
                if (status != null && status.Running)
                {
                    RenderLive(_currentOutboundIface, status.UptimeMs, status.Connections);
                }
            }
            catch { }
        }

        // 3. Connections Tab
        if (MainContentTabs.SelectedIndex == 5)
        {
            RefreshConnections();
        }
    }

    private void SidebarMenu_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainContentTabs == null) return;
        if (sender is ListBox listBox && listBox.SelectedItem is ListBoxItem item && item.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            MainContentTabs.SelectedIndex = idx;
        }
    }

    #region IMainPage implementation (Dashboard)
    public async Task LoadDashboardAsync()
    {
        await _mainVm.LoadAsync(this);
    }

    public void RenderIdle(string profileName, string flow, string sni)
    {
        DashHeadlineText.Text = "SpoofGUI";
        DashHeadlineSub.Text = "Connect and use your X-Ray Client.";
        DashProfileName.Text = profileName;
        DashProfileFlow.Text = flow;
        DashProfileSni.Text = $"SNI: {sni}";
        DashStatUptime.Text = DashStatConns.Text = DashStatIface.Text = "—";
        DashStartBtn.IsEnabled = true;
        DashStopBtn.IsEnabled = false;
    }

    public void RenderLive(string iface, ulong uptimeMs, uint conns)
    {
        _currentOutboundIface = iface;
        DashHeadlineText.Text = "ready";
        DashHeadlineSub.Text = $"Connect and use your X-Ray Client. {conns} active connection{(conns == 1 ? "" : "s")}.";
        DashStatUptime.Text = FormatUptime(uptimeMs);
        DashStatConns.Text = conns.ToString();
        DashStatIface.Text = iface;
        DashStartBtn.IsEnabled = false;
        DashStopBtn.IsEnabled = true;
    }

    public void RenderV2RayCard(bool live, string mode, int socksPort, int httpPort, string lastError)
    {
        DashV2RayStatus.Text = live ? "live" : "idle";
        DashV2RayMode.Text = $"mode: {mode}";
        DashV2RayPorts.Text = $"socks 127.0.0.1:{socksPort} · http 127.0.0.1:{httpPort}";
        DashV2RayError.Text = lastError;
    }

    public void RenderConnecting()
    {
        DashHeadlineText.Text = "starting";
        DashHeadlineSub.Text = "Starting local listener and attaching WinDivert.";
        DashStartBtn.IsEnabled = false;
        DashStopBtn.IsEnabled = false;
    }

    public void RenderError(string message)
    {
        DashHeadlineText.Text = "error";
        DashHeadlineSub.Text = message;
        DashStartBtn.IsEnabled = true;
        DashStopBtn.IsEnabled = false;
    }

    private static string FormatUptime(ulong ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private async void DashStartBtn_Click(object sender, RoutedEventArgs e)
    {
        RenderConnecting();
        await _mainVm.ConnectAsync();
    }

    private async void DashStopBtn_Click(object sender, RoutedEventArgs e)
    {
        await _mainVm.DisconnectAsync();
    }
    #endregion

    #region Profiles Tab
    private void LoadProfiles()
    {
        ProfilesListBox.ItemsSource = null;
        ProfilesListBox.ItemsSource = _configVm.All();
        ProfilesListBox.DisplayMemberPath = "Name";
    }

    private void ProfilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfNameTxt == null || ProfListenHostTxt == null || ProfListenPortTxt == null || 
            ProfConnectIpTxt == null || ProfConnectPortTxt == null || ProfFakeSniTxt == null) return;

        if (sender is ListBox listBox && listBox.SelectedItem is SpoofProfile p)
        {
            ProfNameTxt.Text = p.Name;
            ProfListenHostTxt.Text = p.ListenHost;
            ProfListenPortTxt.Text = p.ListenPort.ToString();
            ProfConnectIpTxt.Text = p.ConnectIp;
            ProfConnectPortTxt.Text = p.ConnectPort.ToString();
            ProfFakeSniTxt.Text = p.FakeSni;
        }
    }

    private void ProfNewBtn_Click(object sender, RoutedEventArgs e)
    {
        ProfilesListBox.SelectedItem = null;
        var p = _configVm.NewDraft();
        ProfNameTxt.Text = p.Name;
        ProfListenHostTxt.Text = p.ListenHost;
        ProfListenPortTxt.Text = p.ListenPort.ToString();
        ProfConnectIpTxt.Text = p.ConnectIp;
        ProfConnectPortTxt.Text = p.ConnectPort.ToString();
        ProfFakeSniTxt.Text = p.FakeSni;
    }

    private void ProfSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = ProfilesListBox.SelectedItem as SpoofProfile;
            var p = selected ?? new SpoofProfile();

            p.Name = ProfNameTxt.Text;
            p.ListenHost = ProfListenHostTxt.Text;
            p.ListenPort = int.Parse(ProfListenPortTxt.Text);
            p.ConnectIp = ProfConnectIpTxt.Text;
            p.ConnectPort = int.Parse(ProfConnectPortTxt.Text);
            p.FakeSni = ProfFakeSniTxt.Text;

            _configVm.Save(p);
            LoadProfiles();
            _ = LoadDashboardAsync();
            MessageBox.Show("Profile saved successfully.", "SpoofGUI", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error saving profile: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ProfActiveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is SpoofProfile p)
        {
            _configVm.SetActive(p.Id);
            LoadProfiles();
            _ = LoadDashboardAsync();
        }
    }

    private void ProfDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is SpoofProfile p)
        {
            var res = MessageBox.Show($"Are you sure you want to delete profile '{p.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                _configVm.Delete(p.Id);
                LoadProfiles();
                _ = LoadDashboardAsync();
            }
        }
    }

    private async void ProfImportDefaultBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var list = await _configVm.FetchSniListAsync();
            var res = _configVm.AddFromEntries(list);
            LoadProfiles();
            MessageBox.Show($"Imported {res.Added} profiles, skipped {res.Skipped}.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import default SNIs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion

    #region V2Ray Tab
    private void LoadV2RayTab()
    {
        V2RayModeComboBox.SelectedIndex = _v2rayVm.V2RayModeIndex;
        LoadSubscriptions();
        LoadV2RayProfiles();
    }

    private void LoadSubscriptions()
    {
        SubscriptionsListBox.ItemsSource = null;
        SubscriptionsListBox.ItemsSource = _v2rayVm.LoadSubscriptions();
        SubscriptionsListBox.DisplayMemberPath = "Name";
    }

    private void LoadV2RayProfiles()
    {
        V2RayProfilesListBox.ItemsSource = null;
        V2RayProfilesListBox.ItemsSource = _v2rayVm.LoadProfiles();
        V2RayProfilesListBox.DisplayMemberPath = "Name";
    }

    private void V2RayModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_v2rayVm == null) return;
        if (sender is ComboBox comboBox && comboBox.SelectedItem is ComboBoxItem item)
        {
            string mode = item.Content.ToString() switch
            {
                string s when s.Contains("Tunnel") => "Tunnel",
                string s when s.Contains("System Proxy") => "SystemProxy",
                _ => "Direct"
            };
            _v2rayVm.SetMode(mode);
            _ = LoadDashboardAsync();
        }
    }

    private void SubscriptionsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubNameTxt == null || SubUrlTxt == null) return;
        if (sender is ListBox listBox && listBox.SelectedItem is Subscription sub)
        {
            SubNameTxt.Text = sub.Name;
            SubUrlTxt.Text = sub.Url;
        }
    }

    private void SubAddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SubNameTxt.Text) || string.IsNullOrWhiteSpace(SubUrlTxt.Text))
        {
            MessageBox.Show("Please fill in both Name and URL.", "V2Ray", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _v2rayVm.AddSubscription(SubNameTxt.Text, SubUrlTxt.Text);
            LoadSubscriptions();
            SubNameTxt.Clear();
            SubUrlTxt.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error adding subscription: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SubUpdateAllBtn_Click(object sender, RoutedEventArgs e)
    {
        SubUpdateAllBtn.IsEnabled = false;
        try
        {
            var res = await _v2rayVm.UpdateAllSubscriptionsAsync();
            LoadV2RayProfiles();
            MessageBox.Show($"Update all completed: {res.Status}", "Update Subscription", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SubUpdateAllBtn.IsEnabled = true;
        }
    }

    private async void SubUpdateSelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SubscriptionsListBox.SelectedItem is Subscription sub)
        {
            SubUpdateSelBtn.IsEnabled = false;
            try
            {
                var res = await _v2rayVm.UpdateSubscriptionAsync(sub);
                LoadV2RayProfiles();
                MessageBox.Show($"Update completed: {res.Status}", "Update Subscription", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SubUpdateSelBtn.IsEnabled = true;
            }
        }
    }

    private void SubDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (SubscriptionsListBox.SelectedItem is Subscription sub)
        {
            var res = MessageBox.Show($"Are you sure you want to delete subscription '{sub.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res == MessageBoxResult.Yes)
            {
                _v2rayVm.DeleteSubscription(sub, true);
                LoadSubscriptions();
                LoadV2RayProfiles();
            }
        }
    }

    private void V2RayImportBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(V2RayImportTxt.Text))
        {
            MessageBox.Show("Please paste a v2ray config link (vmess://, vless://, ss://, trojan://) in the textbox.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var res = _v2rayVm.ImportMany(V2RayImportTxt.Text, "Direct");
            LoadV2RayProfiles();
            V2RayImportTxt.Clear();
            MessageBox.Show($"Imported {res.Imported.Count} profiles. Failures: {res.Failed}. Duplicates skipped: {res.Duplicates}.", "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion

    #region Routing Rules Tab
    private void LoadRoutingRules()
    {
        RoutingRulesListBox.ItemsSource = null;
        try
        {
            var rules = _routingRepo.All();
            RoutingRulesListBox.ItemsSource = rules;
            RoutingRulesListBox.DisplayMemberPath = "Pattern";
        }
        catch { }
    }

    private void RuleAddBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RuleValueTxt.Text))
            return;

        try
        {
            var rule = new RoutingRule
            {
                Kind = (RuleTypeComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "domain",
                Pattern = RuleValueTxt.Text.Trim(),
                Outbound = (RuleActionComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "proxy"
            };

            _routingRepo.Upsert(rule);
            LoadRoutingRules();
            RuleValueTxt.Clear();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to add rule: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion

    #region SNI Scanner Tab
    private CancellationTokenSource? _scanCts;

    private void ScannerStartBtn_Click(object sender, RoutedEventArgs e)
    {
        string host = ScannerTargetHostTxt.Text;
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("Please enter a host or list of domains.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ScannerStartBtn.IsEnabled = false;
        ScannerStopBtn.IsEnabled = true;
        ScannerLogTxt.Text = "SNI Scanner started...\r\n";
        ScannerProgressBar.Value = 0;

        _scanCts = new CancellationTokenSource();

        Task.Run(async () =>
        {
            var progress = new Progress<int>(percent =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ScannerProgressBar.Value = percent;
                });
            });

            try
            {
                var domains = _sniScannerVm.ParseDomains(host);
                var verifyHttp = true;
                var results = await _sniScannerVm.ScanAsync(domains, verifyHttp, progress, _scanCts.Token);
                
                Dispatcher.BeginInvoke(() =>
                {
                    ScannerLogTxt.AppendText($"Scan completed. Scanned {domains.Count} domains. Usable SNIs found: {results.Count(r => r.UsableAsSni)}\r\n");
                    foreach (var r in results.Where(x => x.UsableAsSni))
                    {
                        ScannerLogTxt.AppendText($"[OK] {r.Domain} ({r.TlsMs}ms)\r\n");
                    }
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(() => ScannerLogTxt.AppendText("Scan cancelled.\r\n"));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => ScannerLogTxt.AppendText($"Scan error: {ex.Message}\r\n"));
            }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ScannerStartBtn.IsEnabled = true;
                    ScannerStopBtn.IsEnabled = false;
                });
            }
        });
    }

    private void ScannerStopBtn_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
    }
    #endregion

    #region Active Connections Tab
    private void RefreshConnections()
    {
        try
        {
            var active = _configVm.All().FirstOrDefault(p => p.IsActive);
            var watched = new List<int> { active?.ListenPort ?? 40443, _settingsVm.SocksPort, _settingsVm.HttpPort };

            var conns = NetStats.ActiveConnections(watched);
            ConnectionsListBox.ItemsSource = null;
            if (conns != null)
            {
                ConnectionsListBox.ItemsSource = conns.Select(c => $"TCP  {c.Local}  ->  {c.Remote}   [{c.State}]");
            }
        }
        catch { }
    }

    private void ConnRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshConnections();
    }
    #endregion

    #region Settings Tab
    private void LoadSettings()
    {
        SettingsSocksPortTxt.Text = _settingsVm.SocksPort.ToString();
        SettingsHttpPortTxt.Text = _settingsVm.HttpPort.ToString();
        SettingsRemoteDnsTxt.Text = _settingsVm.RemoteDns;
        SettingsDirectDnsTxt.Text = _settingsVm.DirectDns;
        SettingsBootstrapDnsTxt.Text = _settingsVm.BootstrapDns;
        SettingsCheckUpdatesCb.IsChecked = _settingsVm.CheckUpdatesOnLaunch;
    }

    private void SettingsSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var err = _settingsVm.SavePorts(SettingsSocksPortTxt.Text, SettingsHttpPortTxt.Text);
            if (err != null)
            {
                MessageBox.Show($"Failed to save ports: {err}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _settingsVm.SaveDns(SettingsRemoteDnsTxt.Text, SettingsDirectDnsTxt.Text, SettingsBootstrapDnsTxt.Text);
            _settingsVm.CheckUpdatesOnLaunch = SettingsCheckUpdatesCb.IsChecked ?? false;

            MessageBox.Show("Settings saved successfully.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion

    #region Real-time Logs Tab
    private void UpdateLogsView()
    {
        try
        {
            var snapshot = AppLog.Snapshot();
            var currentLines = LogsOutputTxt.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (snapshot.Count != currentLines.Length - 1)
            {
                LogsOutputTxt.Text = string.Join("\r\n", snapshot);
                LogsOutputTxt.ScrollToEnd();
            }
        }
        catch { }
    }

    private void LogsClearBtn_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Clear();
        LogsOutputTxt.Clear();
    }

    private void LogsExportBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SpoofGUI.log");
            File.WriteAllText(exportPath, LogsOutputTxt.Text);
            MessageBox.Show($"Logs successfully exported to Desktop: {exportPath}", "Logs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to export logs: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion
}
