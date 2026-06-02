using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Engine;
using SpoofGUI.Models;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace SpoofGUI;

public sealed partial class TrayPanelWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly NetStats.BandwidthSampler _sampler = new();
    private Storyboard? _pulse;
    private bool _busy;
    private bool _shown;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayPanelWindow()
    {
        InitializeComponent();
        Title = "SpoofGUI";
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);

        if (_appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;

        _timer.Tick += (_, _) => _ = RefreshAsync();
        Activated += OnActivated;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_shown && !_busy && e.WindowActivationState == WindowActivationState.Deactivated)
            HidePanel();
    }

    public void ShowPanel()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        RootCard.Measure(new Size(420, 1400));
        var desired = RootCard.DesiredSize;
        var w = desired.Width > 10 ? desired.Width : 308;
        var h = desired.Height > 10 ? desired.Height : 384;
        var winW = (int)Math.Ceiling(w * scale);
        var winH = (int)Math.Ceiling(h * scale);

        var px = 0; var py = 0;
        if (GetCursorPos(out var pt)) { px = pt.X; py = pt.Y; }
        var area = DisplayArea.GetFromPoint(new PointInt32(px, py), DisplayAreaFallback.Primary);
        var wa = area.WorkArea;
        var margin = (int)(12 * scale);
        var x = wa.X + wa.Width - winW - margin;
        var y = wa.Y + wa.Height - winH - margin;
        _appWindow.MoveAndResize(new RectInt32(x, y, winW, winH));

        _shown = true;
        _appWindow.Show();
        Activate();
        try { SetForegroundWindow(_hwnd); } catch { }

        _sampler.Reset();
        PopulateProfiles();
        _ = RefreshAsync();
        _timer.Start();
    }

    private void PopulateProfiles()
    {
        try
        {
            var profiles = App.Services.GetRequiredService<ProfileRepository>().All();
            var accent = (SolidColorBrush)Application.Current.Resources["AccentBase"];
            var faint = (SolidColorBrush)Application.Current.Resources["BorderStrong"];
            TrayProfiles.ItemsSource = profiles
                .Select(p => new TrayProfileItem(p.Id, p.Name, $"{p.ConnectIp} · {p.FakeSni}", p.IsActive, p.IsActive ? accent : faint))
                .ToList();
        }
        catch (Exception e)
        {
            AppLog.Warn($"tray profiles: {e.Message}");
        }
    }

    public void HidePanel()
    {
        _shown = false;
        _timer.Stop();
        StopPulse();
        _appWindow.Hide();
    }

    private void OnTrayShowWindow(object sender, RoutedEventArgs e)
    {
        HidePanel();
        App.CurrentWindow?.ShowFromTray();
    }

    private async void OnTrayProfileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TrayProfileItem item) return;
        try
        {
            var profiles = App.Services.GetRequiredService<ProfileRepository>();
            profiles.SetActive(item.Id);

            var engine = App.Services.GetRequiredService<EngineClient>();
            var status = await engine.StatusAsync();
            if (status.Running)
            {
                var guard = App.Services.GetRequiredService<ConnectionGuard>();
                guard.DisarmSni();
                await engine.StopSpoofAsync();
                var active = profiles.GetActive();
                if (active is not null)
                {
                    await engine.StartSpoofAsync(active);
                    guard.ArmSni();
                }
                ShowTrayStatus($"switched + reconnected: {item.Name}");
            }
            else
            {
                ShowTrayStatus($"active profile: {item.Name}");
            }
        }
        catch (Exception ex)
        {
            ShowTrayStatus($"switch failed: {ex.Message}");
        }
        PopulateProfiles();
        await RefreshAsync();
    }

    private void OnTrayQuit(object sender, RoutedEventArgs e)
    {
        HidePanel();
        App.CurrentWindow?.QuitApp();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var engine = App.Services.GetRequiredService<EngineClient>();
            var xray = App.Services.GetRequiredService<XrayCoreService>();
            var tunnel = App.Services.GetRequiredService<SingBoxTunnelService>();
            var active = App.Services.GetRequiredService<ProfileRepository>().GetActive();

            var status = await engine.StatusAsync();
            var sniLive = status.Running;
            var v2Live = xray.IsRunning || tunnel.IsRunning;

            _sampler.Tick();

            _dispatcher.TryEnqueue(() =>
            {
                if (active is not null)
                {
                    TrayProfileName.Text = active.Name;
                    TrayProfileTarget.Text = $"{active.ConnectIp}:{active.ConnectPort}  ·  SNI {active.FakeSni}";
                }
                else
                {
                    TrayProfileName.Text = "no active profile";
                    TrayProfileTarget.Text = "—";
                }

                ApplyLiveVisual(sniLive);

                TrayUptime.Text = sniLive ? FormatUptime(status.UptimeMs) : "—";
                TrayDown.Text = sniLive ? NetStats.FormatRate(_sampler.RecvBps) : "0 B/s";
                TrayUp.Text = sniLive ? NetStats.FormatRate(_sampler.SendBps) : "0 B/s";
                TrayConns.Text = status.Connections.ToString();

                TraySniIcon.Symbol = sniLive ? Symbol.Stop : Symbol.Play;
                TraySniLabel.Text = sniLive ? "disconnect SNI engine" : "connect SNI engine";
                TrayV2RayIcon.Symbol = v2Live ? Symbol.Stop : Symbol.Link;
                TrayV2RayLabel.Text = v2Live ? "disconnect V2Ray" : "connect V2Ray";
            });
        }
        catch (Exception e)
        {
            AppLog.Warn($"tray refresh: {e.Message}");
        }
    }

    private void ApplyLiveVisual(bool live)
    {
        var accent = (SolidColorBrush)Application.Current.Resources["AccentBase"];
        var tertiary = (SolidColorBrush)Application.Current.Resources["TextTertiary"];
        var raised = (SolidColorBrush)Application.Current.Resources["SurfaceRaised"];

        StatusDot.Fill = live ? accent : tertiary;
        StatusPillText.Text = live ? "LIVE" : "IDLE";
        StatusPillText.Foreground = live ? new SolidColorBrush(ColorHelper.FromArgb(255, 21, 24, 30)) : tertiary;
        StatusPill.Background = live ? accent : raised;

        if (live) StartPulse();
        else StopPulse();
    }

    private void StartPulse()
    {
        if (_pulse is not null) return;
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.3,
            Duration = new Duration(TimeSpan.FromMilliseconds(950)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(anim, StatusDot);
        Storyboard.SetTargetProperty(anim, "Opacity");
        _pulse = new Storyboard();
        _pulse.Children.Add(anim);
        _pulse.Begin();
    }

    private void StopPulse()
    {
        if (_pulse is null) return;
        _pulse.Stop();
        _pulse = null;
        StatusDot.Opacity = 1.0;
    }

    private async void OnTraySniToggle(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetTraySniBusy(true);
        try
        {
            var engine = App.Services.GetRequiredService<EngineClient>();
            var status = await engine.StatusAsync();
            var guard = App.Services.GetRequiredService<ConnectionGuard>();
            if (status.Running)
            {
                guard.DisarmSni();
                await engine.StopSpoofAsync();
                ShowTrayStatus("SNI engine stopped");
            }
            else
            {
                var active = App.Services.GetRequiredService<ProfileRepository>().GetActive();
                if (active is null)
                    ShowTrayStatus("no active profile — open SpoofGUI and pick one");
                else
                {
                    await engine.StartSpoofAsync(active);
                    guard.ArmSni();
                    ShowTrayStatus($"SNI engine live · {active.FakeSni}");
                }
            }
        }
        catch (Exception ex)
        {
            ShowTrayStatus($"SNI failed: {ex.Message}");
        }
        finally
        {
            SetTraySniBusy(false);
            _busy = false;
            await RefreshAsync();
        }
    }

    private async void OnTrayV2RayToggle(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        SetTrayV2RayBusy(true);
        try
        {
            var xray = App.Services.GetRequiredService<XrayCoreService>();
            var tunnel = App.Services.GetRequiredService<SingBoxTunnelService>();
            var guard = App.Services.GetRequiredService<ConnectionGuard>();
            if (xray.IsRunning || tunnel.IsRunning)
            {
                guard.DisarmV2Ray();
                await DisconnectV2RayAsync(xray, tunnel);
                ShowTrayStatus("V2Ray disconnected");
            }
            else
            {
                var profile = App.Services.GetRequiredService<V2RayProfileRepository>().All().FirstOrDefault();
                if (profile is null)
                    ShowTrayStatus("no V2Ray config — import one first");
                else
                {
                    var mode = await ConnectV2RayAsync(profile, xray, tunnel);
                    guard.ArmV2Ray();
                    ShowTrayStatus($"V2Ray connected · {profile.Name} ({mode})");
                }
            }
        }
        catch (Exception ex)
        {
            ShowTrayStatus($"V2Ray failed: {ex.Message}");
        }
        finally
        {
            SetTrayV2RayBusy(false);
            _busy = false;
            await RefreshAsync();
        }
    }

    private static async Task<string> ConnectV2RayAsync(V2RayProfile profile, XrayCoreService xray, SingBoxTunnelService tunnel)
    {
        var mode = App.Services.GetRequiredService<AppSettings>().V2RayMode;
        if (string.Equals(mode, "Tunnel", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => tunnel.Start(profile));
            return "tunnel";
        }

        await xray.StartAsync(profile);
        var endpoint = Endpoint(App.Services.GetRequiredService<ProxyPortSettings>());
        if (string.Equals(mode, "SystemProxy", StringComparison.OrdinalIgnoreCase))
        {
            SystemProxy.Enable(endpoint);
            return "system proxy";
        }

        if (SystemProxy.IsOurs(endpoint)) { try { SystemProxy.Disable(); } catch { } }
        return "proxy";
    }

    private static async Task DisconnectV2RayAsync(XrayCoreService xray, SingBoxTunnelService tunnel)
    {
        if (tunnel.IsRunning) await Task.Run(() => tunnel.Stop());
        await xray.StopAsync();
        if (SystemProxy.IsOurs(Endpoint(App.Services.GetRequiredService<ProxyPortSettings>())))
        {
            try { SystemProxy.Disable(); } catch { }
        }
    }

    private void SetTraySniBusy(bool on)
    {
        TraySniButton.IsEnabled = !on;
        TraySniContent.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TraySniSpinner.IsActive = on;
        TraySniSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTrayV2RayBusy(bool on)
    {
        TrayV2RayButton.IsEnabled = !on;
        TrayV2RayContent.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        TrayV2RaySpinner.IsActive = on;
        TrayV2RaySpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowTrayStatus(string text)
    {
        TrayStatusLine.Text = text;
        TrayStatusLine.Visibility = Visibility.Visible;
    }

    private static string Endpoint(ProxyPortSettings ports) =>
        $"http=127.0.0.1:{ports.HttpPort};https=127.0.0.1:{ports.HttpPort};socks=127.0.0.1:{ports.SocksPort}";

    private static string FormatUptime(ulong ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }
}

public sealed record TrayProfileItem(long Id, string Name, string Target, bool IsActive, Microsoft.UI.Xaml.Media.Brush Marker);
