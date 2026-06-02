using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Engine;
using SpoofGUI.GUI;
using WinRT.Interop;

namespace SpoofGUI;

public sealed partial class MainWindow : Window
{
    private AppWindowTitleBar? _titleBar;
    private AppWindow _appWindow = null!;
    private IntPtr _hwnd;
    private bool _exiting;
    private bool _cleaned;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private TrayIconHost? _tray;
    private TrayPanelWindow? _panel;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        Title = "SpoofGUI";
        Closed += OnClosed;

        _hwnd = WindowNative.GetWindowHandle(this);
        var id = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Resize(new Windows.Graphics.SizeInt32 { Width = 1280, Height = 810 });
        _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SpoofGUI.ico"));
        _appWindow.Closing += OnAppWindowClosing;

        if (_appWindow.TitleBar is { } tb)
        {
            _titleBar = tb;
            tb.ExtendsContentIntoTitleBar = true;
            tb.PreferredHeightOption = TitleBarHeightOption.Tall;
        }

        var savedTheme = App.Services.GetRequiredService<SettingsRepository>().Get("theme") ?? "dark";
        ApplyTheme(savedTheme);

        try { InitTray(); }
        catch (Exception e) { AppLog.Error($"tray init failed: {e.Message}"); }
    }

    private void InitTray()
    {
        var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SpoofGUI.ico");
        _tray = new TrayIconHost(ico, "SpoofGUI");
        _tray.LeftClick += () => _dispatcher.TryEnqueue(ShowPanel);
        _tray.RightClick += () => _dispatcher.TryEnqueue(ShowPanel);
        _tray.DoubleClick += () => _dispatcher.TryEnqueue(ShowFromTray);
    }

    private void ShowPanel()
    {
        try
        {
            _panel ??= new TrayPanelWindow();
            _panel.ShowPanel();
        }
        catch (Exception e)
        {
            AppLog.Warn($"tray panel: {e.Message}");
            ShowFromTray();
        }
    }

    public void ShowFromTray() => _dispatcher.TryEnqueue(() =>
    {
        _appWindow.Show();
        _appWindow.MoveInZOrderAtTop();
        Activate();
        try { SetForegroundWindow(_hwnd); } catch { }
    });

    public void QuitApp()
    {
        _exiting = true;
        Cleanup();
        Application.Current.Exit();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        sender.Hide();
    }

    private void OnClosed(object sender, WindowEventArgs args) => Cleanup();

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        try { _tray?.Dispose(); } catch { }
        try { _panel?.Close(); } catch { }
        try { App.Services.GetRequiredService<ConnectionGuard>().Dispose(); } catch { }
        try { App.Services.GetRequiredService<SingBoxTunnelService>().Stop(); } catch { }
        try { App.Services.GetRequiredService<XrayCoreService>().Dispose(); } catch { }
        try { App.Services.GetRequiredService<EngineSupervisor>().Stop(); } catch { }
        try
        {
            var ports = App.Services.GetRequiredService<ProxyPortSettings>();
            var endpoint = $"http=127.0.0.1:{ports.HttpPort};https=127.0.0.1:{ports.HttpPort};socks=127.0.0.1:{ports.SocksPort}";
            if (SystemProxy.IsOurs(endpoint)) SystemProxy.Disable();
        }
        catch { }
    }

    public void ApplyTheme(string theme)
    {
        var requestedTheme = ThemeService.Apply(theme, RootShell);
        ApplyTitleBarTheme(requestedTheme);
    }

    private void ApplyTitleBarTheme(ElementTheme theme)
    {
        if (_titleBar is null)
            return;

        if (theme == ElementTheme.Light)
        {
            _titleBar.ButtonBackgroundColor = ColorHelper.FromArgb(255, 246, 247, 249);
            _titleBar.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(255, 246, 247, 249);
            _titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 235, 238, 243);
            _titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 212, 218, 227);
            _titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 24, 27, 33);
            _titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 113, 123, 138);
            _titleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(255, 24, 27, 33);
            _titleBar.ButtonPressedForegroundColor = ColorHelper.FromArgb(255, 24, 27, 33);
            return;
        }

        _titleBar.ButtonBackgroundColor = ColorHelper.FromArgb(255, 27, 30, 37);
        _titleBar.ButtonInactiveBackgroundColor = ColorHelper.FromArgb(255, 27, 30, 37);
        _titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(255, 34, 38, 47);
        _titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(255, 21, 24, 30);
        _titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 242, 243, 245);
        _titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 168, 173, 183);
        _titleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(255, 242, 243, 245);
        _titleBar.ButtonPressedForegroundColor = ColorHelper.FromArgb(255, 242, 243, 245);
    }
}
