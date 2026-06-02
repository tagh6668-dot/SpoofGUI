using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpoofGUI.Core;
using SpoofGUI.GUI.ViewModels;

namespace SpoofGUI.GUI.Pages;

public sealed partial class SniScannerPage : Page
{
    private readonly SniScannerPageViewModel _vm;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private bool _scanning;
    private IReadOnlyList<SniScanResult> _lastResults = [];

    public SniScannerPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<SniScannerPageViewModel>();
        BuiltinToggleLabel.Text = $"scan built-in Cloudflare list ({_vm.BuiltinCloudflareList().Count})";
    }

    private async void OnScan(object sender, object e)
    {
        if (_scanning) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domains = new List<string>();
        foreach (var d in _vm.ParseDomains(DomainsBox.Text))
            if (seen.Add(d)) domains.Add(d);

        var profileCount = 0;
        if (IncludeProfilesToggle.IsOn)
        {
            foreach (var sni in _vm.ProfileSnis())
                if (seen.Add(sni)) { domains.Add(sni); profileCount++; }
        }

        var builtinCount = 0;
        if (IncludeBuiltinToggle.IsOn)
        {
            foreach (var sni in _vm.BuiltinCloudflareList())
                if (seen.Add(sni)) { domains.Add(sni); builtinCount++; }
        }

        if (domains.Count == 0)
        {
            ScanStatus.Text = "enter a hostname, or enable the Configs / built-in Cloudflare list toggle";
            return;
        }

        var capped = domains.Count > SniScannerService.MaxDomains;
        if (capped) domains = domains.Take(SniScannerService.MaxDomains).ToList();

        SetScanning(true);
        var total = domains.Count;
        var extras = new List<string>();
        if (profileCount > 0) extras.Add($"{profileCount} from Configs");
        if (builtinCount > 0) extras.Add($"{builtinCount} from Cloudflare list");
        var note = extras.Count > 0 ? $" (incl. {string.Join(", ", extras)})" : "";
        ScanStatus.Text = capped ? $"scanning {total} (capped from more)…" : $"scanning 0 / {total}{note}…";

        var progress = new Progress<int>(done =>
            _dispatcher.TryEnqueue(() => ScanStatus.Text = $"scanning {done} / {total}…"));

        try
        {
            var results = await _vm.ScanAsync(domains, VerifyHttpToggle.IsOn, progress, CancellationToken.None);
            _lastResults = results;
            ResultsList.ItemsSource = results;
            UseBestButton.IsEnabled = results.Any(r => r.UsableAsSni || (r.IsCloudflare && r.TlsOk));
            var usable = results.Count(r => r.UsableAsSni);
            ResultSummary.Text = $"{usable} usable · {results.Count} checked";
            ScanStatus.Text = usable > 0 ? $"done — {usable} usable Fake SNI target(s)" : "done — no usable targets";
        }
        catch (Exception ex)
        {
            ScanStatus.Text = $"scan failed: {ex.Message}";
        }
        finally
        {
            SetScanning(false);
        }
    }

    private void OnClear(object sender, object e)
    {
        DomainsBox.Text = "";
        ResultsList.ItemsSource = null;
        _lastResults = [];
        UseBestButton.IsEnabled = false;
        ResultSummary.Text = "";
        ScanStatus.Text = "";
    }

    private void OnUseBest(object sender, object e)
    {
        ScanStatus.Text = _vm.ApplyBestToActive(_lastResults);
    }

    private void OnUseDomain(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: SniScanResult result }) return;

        ScanStatus.Text = _vm.CreateProfileFromResult(result);
    }

    private void SetScanning(bool on)
    {
        _scanning = on;
        ScanButton.IsEnabled = !on;
        ClearButton.IsEnabled = !on;
        ScanButtonLabel.Text = on ? "scanning…" : "scan";
        ScanSpinner.IsActive = on;
        ScanSpinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }
}
