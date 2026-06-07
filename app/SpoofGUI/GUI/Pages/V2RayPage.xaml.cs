using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SpoofGUI.Core;
using SpoofGUI.GUI.ViewModels;
using SpoofGUI.Models;
using Windows.ApplicationModel.DataTransfer;

namespace SpoofGUI.GUI.Pages;

public sealed partial class V2RayPage : Page
{
    private readonly V2RayPageViewModel _vm;
    private V2RayProfile? _selected;
    private bool _ready;
    private bool _pinging;
    private bool _pingCancelRequested;
    private CancellationTokenSource? _pingCts;
    private bool _xrayRunning;
    private bool _systemProxyActive;
    private bool _tunnelActive;
    private IReadOnlyList<V2RayProfile> _allProfiles = [];
    private List<V2RayProfile> _visibleProfiles = new();
    private int _pingDone;
    private int _pingFailed;
    private int _pingTotal;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly NetStats.BandwidthSampler _sampler = new();
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _connectedAt;
    private const int GraphSamples = 60;
    private readonly Queue<double> _downHist = new();
    private readonly Queue<double> _upHist = new();

    public V2RayPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<V2RayPageViewModel>();
        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _statsTimer.Stop();
        _statsTimer.Tick += (_, _) => UpdateStats();
        GraphHost.SizeChanged += (_, _) => RedrawGraph();
        _ready = true;
    }

    private void UpdateStats()
    {
        if (!_xrayRunning)
        {
            StatStatus.Text = "idle";
            StatUptime.Text = "—";
            StatDown.Text = "0 B/s";
            StatUp.Text = "0 B/s";
            StatTotal.Text = NetStats.FormatBytes(_sampler.TotalBytesRecv) + " / " + NetStats.FormatBytes(_sampler.TotalBytesSent);
            PushGraph(0, 0);
            return;
        }
        _sampler.Tick();
        StatStatus.Text = _tunnelActive ? "live · tunnel" : _systemProxyActive ? "live · system proxy" : "live";
        if (_connectedAt is DateTime t)
        {
            var up = DateTime.UtcNow - t;
            StatUptime.Text = up.TotalHours >= 1
                ? $"{(int)up.TotalHours}h {up.Minutes:D2}m"
                : $"{up.Minutes:D2}:{up.Seconds:D2}";
        }
        StatDown.Text = NetStats.FormatRate(_sampler.RecvBps);
        StatUp.Text = NetStats.FormatRate(_sampler.SendBps);
        StatTotal.Text = NetStats.FormatBytes(_sampler.TotalBytesRecv) + " / " + NetStats.FormatBytes(_sampler.TotalBytesSent);
        PushGraph(_sampler.RecvBps, _sampler.SendBps);
    }

    private void PushGraph(double down, double up)
    {
        _downHist.Enqueue(down);
        _upHist.Enqueue(up);
        while (_downHist.Count > GraphSamples) _downHist.Dequeue();
        while (_upHist.Count > GraphSamples) _upHist.Dequeue();
        RedrawGraph();
    }

    private void RedrawGraph()
    {
        var w = GraphHost.ActualWidth;
        var h = GraphHost.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var peak = Math.Max(1.0, Math.Max(
            _downHist.Count > 0 ? _downHist.Max() : 0,
            _upHist.Count > 0 ? _upHist.Max() : 0));
        GraphPeak.Text = "peak " + NetStats.FormatRate(peak);
        DownLine.Points = BuildPoints(_downHist, w, h, peak);
        UpLine.Points = BuildPoints(_upHist, w, h, peak);
    }

    private static PointCollection BuildPoints(Queue<double> history, double w, double h, double max)
    {
        var points = new PointCollection();
        var values = history.ToArray();
        if (values.Length == 0) return points;
        var stepX = values.Length > 1 ? w / (values.Length - 1) : w;
        for (var i = 0; i < values.Length; i++)
        {
            var x = i * stepX;
            var y = h - values[i] / max * (h - 2) - 1;
            points.Add(new Windows.Foundation.Point(x, y));
        }
        return points;
    }

    private async Task LoadAsync()
    {
        ModeSelector.SelectedIndex = _vm.V2RayModeIndex;
        try
        {
            Reload();
            CoreStatusText.Text = File.Exists(Paths.SingBoxExePath)
                ? "sing-box core: bundled"
                : "sing-box core: missing";
        }
        catch (Exception ex)
        {
            CoreStatusText.Text = "sing-box unavailable";
            StatusText.Text = ex.Message;
        }

        bool xrayUp;
        try { xrayUp = await _vm.RefreshRunningAsync(); }
        catch { xrayUp = _vm.IsRunning; }

        _tunnelActive = _vm.TunnelRunning;
        _xrayRunning = xrayUp || _tunnelActive;

        _systemProxyActive = SystemProxy.IsEnabled();
        if (_xrayRunning && _connectedAt is null) _connectedAt = DateTime.UtcNow;
        if (!_statsTimer.IsEnabled) _statsTimer.Start();
        UpdateStats();
        RenderActionState();
    }

    private static string ModeFromIndex(int idx) => idx switch
    {
        2 => "SystemProxy",
        1 => "Tunnel",
        _ => "Proxy",
    };

    private string CurrentMode() => ModeFromIndex(ModeSelector.SelectedIndex);

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _vm.SetMode(CurrentMode());
    }

    private void Reload()
    {
        _allProfiles = _vm.LoadProfiles();
        ApplyProfileView();
        if (_selected is not null)
        {
            ProfileList.SelectedItem = _allProfiles.FirstOrDefault(p => p.Id == _selected.Id);
        }
    }

    private void ApplyProfileView()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<V2RayProfile> profiles = _allProfiles;
        if (!string.IsNullOrWhiteSpace(query))
        {
            profiles = profiles.Where(p =>
                p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Address.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                p.ServerName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        profiles = SortSelector.SelectedIndex switch
        {
            1 => profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            2 => profiles.OrderBy(p => p.Protocol, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            3 => profiles.OrderBy(PingSortValue).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => profiles,
        };

        _visibleProfiles = profiles.ToList();
        ProfilesSource.Source = _visibleProfiles
            .GroupBy(p => p.GroupLabel)
            .OrderBy(g => g.Key == "Ungrouped")
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ConfigGroup(g.Key, g))
            .ToList();
        RenderActionState();
    }

    private static int PingSortValue(V2RayProfile p)
    {
        var text = p.Ping.Replace(" ms", "", StringComparison.OrdinalIgnoreCase).Trim();
        return int.TryParse(text, out var ms) ? ms : int.MaxValue;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyProfileView();
    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) ApplyProfileView();
    }

    private async void OnImport(object sender, object e)
    {
        if (string.IsNullOrWhiteSpace(ImportText.Text)) return;

        try
        {
            var preview = _vm.PreviewImport(ImportText.Text);
            if (preview.Items.Count > 0)
            {
                var rows = preview.Items.Take(12)
                    .Select(i => $"{(i.Duplicate ? "dup " : "")}{i.Protocol} {i.Address}:{i.Port}  {i.Name}")
                    .ToList();
                if (preview.Items.Count > rows.Count) rows.Add($"... {preview.Items.Count - rows.Count} more");
                var dialog = new ContentDialog
                {
                    Title = "Import V2Ray configs?",
                    Content = string.Join(Environment.NewLine, rows) + Environment.NewLine + $"invalid: {preview.Invalid}, duplicates: {preview.Duplicates}",
                    PrimaryButtonText = "import",
                    CloseButtonText = "cancel",
                    XamlRoot = XamlRoot,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            var result = _vm.ImportMany(ImportText.Text, CurrentMode());
            if (result.Imported.Count == 0)
            {
                StatusText.Text = result.Failed > 0
                    ? $"import failed: {result.Failed} invalid config(s)"
                    : result.Duplicates > 0
                        ? $"skipped {result.Duplicates} duplicate config(s)"
                        : "nothing to import";
            }
            else
            {
                _selected = result.Imported[^1];
                ImportText.Text = "";
                var summary = result.Imported.Count == 1
                    ? $"imported: {_selected.Name}"
                    : $"imported {result.Imported.Count} configs";
                if (result.Failed > 0) summary += $" ({result.Failed} skipped)";
                if (result.Duplicates > 0) summary += $" ({result.Duplicates} duplicate)";
                StatusText.Text = summary;
                Reload();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"import failed: {ex.Message}";
        }
        RenderActionState();
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = ProfileList.SelectedItem as V2RayProfile;
        RenderActionState();
    }

    private async void OnConnect(object sender, object e)
    {
        if (_selected is null)
        {
            StatusText.Text = "select a config first";
            return;
        }

        ConnectButton.IsEnabled = false;
        SetConnecting(true);

        var mode = CurrentMode();
        if (string.Equals(mode, "Tunnel", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "connecting… (sing-box tunnel)";
            try
            {
                await _vm.StartTunnelAsync(_selected);
                _xrayRunning = true;
                _tunnelActive = true;
                _vm.ArmKillSwitch();
                _sampler.Reset();
                _connectedAt = DateTime.UtcNow;
                _systemProxyActive = false;
                StatusText.Text = "connected + tunnel (sing-box routing all traffic)";
            }
            catch (Exception tx)
            {
                try { await _vm.StopTunnelAsync(); } catch { }
                _xrayRunning = false;
                _tunnelActive = false;
                StatusText.Text = $"tunnel failed: {tx.Message}";
            }
            RenderActionState();
            return;
        }

        StatusText.Text = "starting xray...";
        try
        {
            await _vm.StartAsync(_selected);
            _xrayRunning = true;
            _vm.ArmKillSwitch();
            _sampler.Reset();
            _connectedAt = DateTime.UtcNow;
            if (string.Equals(mode, "SystemProxy", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    SystemProxy.Enable(_vm.SystemProxyEndpoint);
                    _systemProxyActive = true;
                    StatusText.Text = "connected + system proxy active";
                }
                catch (Exception px)
                {
                    StatusText.Text = $"connected (proxy set failed: {px.Message})";
                }
            }
            else
            {

                if (SystemProxy.IsOurs(_vm.SystemProxyEndpoint))
                {
                    try { SystemProxy.Disable(); } catch { }
                }
                _systemProxyActive = false;
                StatusText.Text = $"connected: socks 127.0.0.1:{_vm.SocksPort}, http 127.0.0.1:{_vm.HttpPort}";
            }
        }
        catch (Exception ex)
        {
            _xrayRunning = false;
            StatusText.Text = $"connect failed: {ex.Message}";
        }
        RenderActionState();
    }

    private async void OnStop(object sender, object e)
    {
        StopButton.IsEnabled = false;
        StatusText.Text = "stopping...";
        _vm.DisarmKillSwitch();

        if (_vm.TunnelRunning)
        {
            try { await _vm.StopTunnelAsync(); } catch (Exception tx) { AppLog.Warn($"tunnel stop: {tx.Message}"); }
        }
        _tunnelActive = false;

        try { await _vm.StopAsync(); }
        catch (Exception ex) { StatusText.Text = $"stop failed: {ex.Message}"; }
        _xrayRunning = false;
        _connectedAt = null;
        if (_systemProxyActive)
        {
            try { SystemProxy.Disable(); _systemProxyActive = false; } catch { }
        }
        StatusText.Text = "stopped";
        UpdateStats();
        RenderActionState();
    }

    private async void OnNew(object sender, object e)
    {
        var profile = new V2RayProfile();
        if (await ShowEditorAsync(profile))
        {
            _vm.Save(profile);
            _selected = profile;
            StatusText.Text = $"saved: {profile.Name}";
            Reload();
        }
        RenderActionState();
    }

    private async void OnEdit(object sender, object e)
    {
        if (_selected is null)
        {
            StatusText.Text = "select a config first";
            return;
        }

        var draft = Clone(_selected);
        if (await ShowEditorAsync(draft))
        {
            _vm.Save(draft);
            _selected = draft;
            StatusText.Text = $"saved: {draft.Name}";
            Reload();
        }
        RenderActionState();
    }

    private async void OnDelete(object sender, object e)
    {
        var selected = ProfileList.SelectedItems.OfType<V2RayProfile>().Where(p => p.Id != 0).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "select a config first";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = selected.Count == 1 ? "Delete config?" : $"Delete {selected.Count} configs?",
            Content = selected.Count == 1 ? selected[0].Name : "Selected V2Ray configs will be removed.",
            PrimaryButtonText = "delete",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var profile in selected) _vm.Delete(profile);
        _selected = null;
        StatusText.Text = selected.Count == 1 ? $"deleted: {selected[0].Name}" : $"deleted {selected.Count} configs";
        Reload();
        RenderActionState();
    }

    private async Task<bool> ShowEditorAsync(V2RayProfile profile)
    {
        var name = Field("Name", profile.Name);
        var protocol = Field("Protocol", profile.Protocol);
        var address = Field("Address", profile.Address);
        var port = Field("Port", profile.Port.ToString());
        var userId = Field("UUID / password", profile.UserId);
        var security = Field("Security", profile.Security);
        var transport = Field("Transport", profile.Transport);
        var serverName = Field("SNI", profile.ServerName);
        var group = Field("Group", profile.GroupName);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(name.Container);
        panel.Children.Add(protocol.Container);
        panel.Children.Add(address.Container);
        panel.Children.Add(port.Container);
        panel.Children.Add(userId.Container);
        panel.Children.Add(security.Container);
        panel.Children.Add(transport.Container);
        panel.Children.Add(serverName.Container);
        panel.Children.Add(group.Container);

        var dialog = new ContentDialog
        {
            Title = "Edit config",
            Content = panel,
            PrimaryButtonText = "save",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return false;
        }

        if (!int.TryParse(port.Box.Text, out var parsedPort) || parsedPort <= 0 || parsedPort > 65535)
        {
            StatusText.Text = "invalid port";
            return false;
        }

        profile.Name = name.Box.Text.Trim();
        profile.Protocol = protocol.Box.Text.Trim().ToLowerInvariant();
        profile.Address = address.Box.Text.Trim();
        profile.Port = parsedPort;
        profile.UserId = userId.Box.Text.Trim();
        profile.Security = security.Box.Text.Trim();
        profile.Transport = transport.Box.Text.Trim();
        profile.ServerName = serverName.Box.Text.Trim();
        profile.GroupName = group.Box.Text.Trim();
        return true;
    }

    private (StackPanel Container, TextBox Box) Field(string label, string value)
    {
        var box = new TextBox
        {
            Text = value,
            Style = (Style)Application.Current.Resources["FieldTextBox"],
        };
        var container = new StackPanel();
        container.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["FieldLabel"],
        });
        container.Children.Add(box);
        return (container, box);
    }

    private void SetConnecting(bool on)
    {
        ConnectSpinner.IsActive = on;
        ConnectSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ConnectContent.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderActionState()
    {
        SetConnecting(false);
        var count = ProfileList.SelectedItems.Count;
        ConnectButton.IsEnabled = count == 1 && !_xrayRunning;
        StopButton.IsEnabled = _xrayRunning;
        PingButton.IsEnabled = !_pinging;
        EditButton.IsEnabled = count == 1;
        DeleteButton.IsEnabled = count >= 1;
        FastestButton.IsEnabled = !_pinging && !_xrayRunning && _visibleProfiles.Count > 0;
    }

    private void SetPing(V2RayProfile profile, string value) =>
        _dispatcher.TryEnqueue(() =>
        {
            profile.Ping = value;
            _vm.RememberPing(profile.Id, value);
        });

    private async Task PingProfileAsync(V2RayProfile profile, SemaphoreSlim gate, CancellationToken ct)
    {
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }

        SetPing(profile, "…");
        try
        {
            var ms = await _vm.TestRealDelayAsync(profile, ct);
            SetPing(profile, $"{ms} ms");
            _vm.RecordPing(profile.Id, ms);
            _dispatcher.TryEnqueue(() => profile.LatencySummary = _vm.LatencySummary(profile.Id));
        }
        catch (OperationCanceledException)
        {
            SetPing(profile, "—");
        }
        catch
        {
            SetPing(profile, "timeout");
            Interlocked.Increment(ref _pingFailed);
        }
        finally
        {
            var done = Interlocked.Increment(ref _pingDone);
            var failed = Volatile.Read(ref _pingFailed);
            _dispatcher.TryEnqueue(() =>
            {
                if (_pinging) StatusText.Text = $"pinging {done}/{_pingTotal} config(s), {failed} failed";
            });
            gate.Release();
        }
    }

    private void SetPinging(bool on)
    {
        _pinging = on;
        PingButton.IsEnabled = !on;
        PingButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        CancelPingButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        CancelPingButton.IsEnabled = on;
        _pingCancelRequested = false;
        CancelPingLabel.Text = "cancel ping";
        PingLabel.Text = on ? "pinging…" : "ping all";
        PingIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PingSpinner.IsActive = on;
        PingSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) RenderActionState();
    }

    private void OnCancelPing(object sender, object e)
    {
        if (!_pinging) return;
        if (_pingCancelRequested) return;
        _pingCancelRequested = true;
        CancelPingLabel.Text = "cancelling...";
        StatusText.Text = "cancelling…";
        _pingCts?.Cancel();
    }

    private async void OnPingAll(object sender, object e)
    {
        if (_pinging) return;
        var profiles = _visibleProfiles;
        if (profiles.Count == 0)
        {
            StatusText.Text = "no configs to ping";
            return;
        }

        _pingCts?.Dispose();
        _pingCts = new CancellationTokenSource();
        var token = _pingCts.Token;

        SetPinging(true);
        _pingDone = 0;
        _pingFailed = 0;
        _pingTotal = profiles.Count;
        StatusText.Text = $"pinging {profiles.Count} config(s)…";

        try
        {
            await Task.Run(async () =>
            {
                using var gate = new SemaphoreSlim(3);
                await Task.WhenAll(profiles.Select(p => PingProfileAsync(p, gate, token)));
            });
            StatusText.Text = token.IsCancellationRequested
                ? "ping cancelled"
                : $"ping complete: {_pingTotal - _pingFailed}/{_pingTotal} ok, {_pingFailed} failed";
        }
        finally
        {
            SetPinging(false);
            _pingCts?.Dispose();
            _pingCts = null;
        }
    }

    private async void OnConnectFastest(object sender, object e)
    {
        if (_pinging || _xrayRunning) return;
        var profiles = _visibleProfiles;
        if (profiles.Count == 0)
        {
            StatusText.Text = "no configs to test";
            return;
        }

        SetPinging(true);
        _pingDone = 0;
        _pingFailed = 0;
        _pingTotal = profiles.Count;
        StatusText.Text = "finding fastest config...";

        V2RayProfile? best = null;
        long bestMs = long.MaxValue;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(12, profiles.Count * 6)));
        try
        {
            foreach (var profile in profiles)
            {
                try
                {
                    SetPing(profile, "...");
                    var ms = await _vm.TestRealDelayAsync(profile, cts.Token);
                    SetPing(profile, $"{ms} ms");
                    _vm.RecordPing(profile.Id, ms);
                    profile.LatencySummary = _vm.LatencySummary(profile.Id);
                    if (ms < bestMs) { bestMs = ms; best = profile; }
                }
                catch
                {
                    SetPing(profile, "timeout");
                    _pingFailed++;
                }
                finally
                {
                    _pingDone++;
                    StatusText.Text = $"testing {_pingDone}/{_pingTotal}, best {(best is null ? "none" : best.Name)}";
                }
            }
        }
        finally
        {
            SetPinging(false);
        }

        if (best is null)
        {
            StatusText.Text = "no working config found";
            return;
        }

        _selected = best;
        ProfileList.SelectedItems.Clear();
        ProfileList.SelectedItem = best;
        StatusText.Text = $"connecting fastest: {best.Name} ({bestMs} ms)";
        OnConnect(sender, e);
    }

    private async void OnPingOne(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: V2RayProfile profile }) return;
        _pingDone = 0;
        _pingFailed = 0;
        _pingTotal = 1;
        await Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(1);
            await PingProfileAsync(profile, gate, CancellationToken.None);
        });
        StatusText.Text = $"{profile.Name}: {profile.Ping}";
    }

    private void OnSelectConfig(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: V2RayProfile profile }) return;
        if (!ProfileList.SelectedItems.Contains(profile))
            ProfileList.SelectedItems.Add(profile);
        _selected = profile;
        StatusText.Text = $"selected {ProfileList.SelectedItems.Count} config(s)";
        RenderActionState();
    }

    private async void OnSetGroup(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: V2RayProfile profile }) return;

        var box = new TextBox
        {
            Text = profile.GroupName,
            PlaceholderText = "group name (blank = ungrouped)",
            Style = (Style)Application.Current.Resources["FieldTextBox"],
        };
        var dialog = new ContentDialog
        {
            Title = "Set group",
            Content = box,
            PrimaryButtonText = "save",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        profile.GroupName = box.Text.Trim();
        _vm.Save(profile);
        StatusText.Text = string.IsNullOrEmpty(profile.GroupName)
            ? $"{profile.Name}: ungrouped"
            : $"{profile.Name} → {profile.GroupName}";
        Reload();
    }

    private void OnExportConfig(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: V2RayProfile profile }) return;
        var text = string.IsNullOrWhiteSpace(profile.RawUri) ? profile.Address : profile.RawUri;
        if (string.IsNullOrEmpty(text))
        {
            StatusText.Text = "nothing to export";
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        StatusText.Text = $"copied to clipboard: {profile.Name}";
    }

    private static Style Res(string key) => (Style)Application.Current.Resources[key];
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private async void OnManageSubscriptions(object sender, object e)
    {
        var nameBox = new TextBox { PlaceholderText = "name (optional)", Style = Res("FieldTextBox") };
        var urlBox = new TextBox { PlaceholderText = "https://… subscription url", Style = Res("FieldTextBox") };
        var listPanel = new StackPanel { Spacing = 8 };
        var status = new TextBlock { Style = Res("TextCaption"), Foreground = Brush("TextSecondary"), TextWrapping = TextWrapping.Wrap };

        void Rebuild()
        {
            listPanel.Children.Clear();
            var subs = _vm.LoadSubscriptions();
            if (subs.Count == 0)
            {
                listPanel.Children.Add(new TextBlock { Text = "no subscriptions yet", Style = Res("TextCaption"), Foreground = Brush("TextTertiary") });
                return;
            }
            foreach (var sub in subs) listPanel.Children.Add(BuildSubscriptionRow(sub, Rebuild, status));
        }

        var addButton = new Button { Style = Res("PrimaryButton") };
        addButton.Content = new TextBlock { Text = "add + fetch" };
        addButton.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(urlBox.Text)) { status.Text = "url required"; return; }
            addButton.IsEnabled = false;
            var sub = _vm.AddSubscription(nameBox.Text, urlBox.Text);
            nameBox.Text = "";
            urlBox.Text = "";
            status.Text = $"fetching {sub.Name}…";
            Rebuild();
            var result = await _vm.UpdateSubscriptionAsync(sub);
            status.Text = result.Status == "ok" ? $"{sub.Name}: {result.Count} configs imported" : $"{sub.Name}: {result.Status}";
            addButton.IsEnabled = true;
            Rebuild();
            Reload();
        };

        var updateAllButton = new Button { Style = Res("SecondaryButton") };
        updateAllButton.Content = new TextBlock { Text = "update all" };
        updateAllButton.Click += async (_, _) =>
        {
            updateAllButton.IsEnabled = false;
            status.Text = "updating all…";
            var result = await _vm.UpdateAllSubscriptionsAsync();
            status.Text = result.Status;
            updateAllButton.IsEnabled = true;
            Rebuild();
            Reload();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(addButton);
        actions.Children.Add(updateAllButton);

        var container = new StackPanel { Spacing = 12, MinWidth = 480 };
        container.Children.Add(new TextBlock { Text = "Add subscription", Style = Res("FieldLabel") });
        container.Children.Add(nameBox);
        container.Children.Add(urlBox);
        container.Children.Add(actions);
        container.Children.Add(new Border { Style = Res("SectionDivider"), Margin = new Thickness(0) });
        container.Children.Add(new ScrollViewer { Content = listPanel, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        container.Children.Add(status);

        Rebuild();

        var dialog = new ContentDialog
        {
            Title = "Subscriptions",
            Content = container,
            CloseButtonText = "done",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
        Reload();
    }

    private FrameworkElement BuildSubscriptionRow(Subscription sub, Action rebuild, TextBlock status)
    {
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = sub.Name, Style = Res("TextBody"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = sub.StatusLine, Style = Res("TextCaption"), Foreground = Brush("TextSecondary") });

        var auto = new ToggleSwitch { IsOn = sub.AutoUpdate, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        auto.Toggled += (_, _) => _vm.SetSubscriptionAutoUpdate(sub, auto.IsOn);

        var update = new Button { Background = Brush("SurfaceBase"), Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        update.Content = new SymbolIcon(Symbol.Sync) { Foreground = Brush("AccentBase") };
        update.Click += async (_, _) =>
        {
            update.IsEnabled = false;
            status.Text = $"updating {sub.Name}…";
            var result = await _vm.UpdateSubscriptionAsync(sub);
            status.Text = result.Status == "ok" ? $"{sub.Name}: {result.Count} configs" : $"{sub.Name}: {result.Status}";
            update.IsEnabled = true;
            rebuild();
            Reload();
        };

        var del = new Button { Background = Brush("SurfaceBase"), Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
        del.Content = new SymbolIcon(Symbol.Delete) { Foreground = Brush("StatusDanger") };
        del.Click += (_, _) =>
        {
            _vm.DeleteSubscription(sub, removeProfiles: true);
            status.Text = $"deleted: {sub.Name} (configs removed)";
            rebuild();
            Reload();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnSpacing = 8;
        Grid.SetColumn(info, 0);
        Grid.SetColumn(auto, 1);
        Grid.SetColumn(update, 2);
        Grid.SetColumn(del, 3);
        grid.Children.Add(info);
        grid.Children.Add(auto);
        grid.Children.Add(update);
        grid.Children.Add(del);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brush("SurfaceSunken"),
            BorderBrush = Brush("BorderSubtle"),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    private static V2RayProfile Clone(V2RayProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Protocol = p.Protocol,
        Mode = p.Mode,
        Address = p.Address,
        Port = p.Port,
        UserId = p.UserId,
        Security = p.Security,
        Transport = p.Transport,
        ServerName = p.ServerName,
        RawUri = p.RawUri,
        SubscriptionId = p.SubscriptionId,
        GroupName = p.GroupName,
    };
}

public sealed class ConfigGroup : List<V2RayProfile>
{
    public string Key { get; }
    public ConfigGroup(string key, IEnumerable<V2RayProfile> items) : base(items) => Key = key;
}
