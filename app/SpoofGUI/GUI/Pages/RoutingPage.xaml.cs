using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpoofGUI.Core;
using SpoofGUI.GUI.ViewModels;
using SpoofGUI.Models;
using WinRT.Interop;

namespace SpoofGUI.GUI.Pages;

public sealed partial class RoutingPage : Page
{
    private readonly RoutingPageViewModel _vm;
    private readonly List<long> _chainIds = new();
    private IReadOnlyList<RoutingRule> _currentRules = [];
    private readonly Dictionary<long, bool> _enabledSnapshot = new();
    private bool _ready;

    public RoutingPage()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<RoutingPageViewModel>();
        Loaded += (_, _) => LoadAll();
    }

    private void LoadAll()
    {
        _ready = false;
        ModeWarning.Visibility = _vm.TunnelMode ? Visibility.Collapsed : Visibility.Visible;

        var rules = _vm.LoadRules();
        _currentRules = rules;
        _enabledSnapshot.Clear();
        foreach (var r in rules) _enabledSnapshot[r.Id] = r.Enabled;
        RuleList.ItemsSource = rules;
        RuleCountText.Text = rules.Count == 1 ? "1 rule" : $"{rules.Count} rules";

        _chainIds.Clear();
        _chainIds.AddRange(_vm.LoadChain().Select(h => h.ProfileId));
        RenderChain();
        _ready = true;
    }

    private void RenderChain() => ChainList.ItemsSource = _vm.LoadChain();

    private async void OnAddRule(object sender, object e)
    {
        var rule = new RoutingRule();
        if (await ShowRuleEditorAsync(rule, "Add rule"))
        {
            _vm.SaveRule(rule);
            LoadAll();
            StatusText.Text = $"added: {rule.Pattern} · {await _vm.ApplyAsync()}";
        }
    }

    private async Task<bool> ShowRuleEditorAsync(RoutingRule rule, string title)
    {
        var kind = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var k in RoutingRule.Kinds) kind.Items.Add(k);
        kind.SelectedItem = RoutingRule.Kinds.Contains(rule.Kind) ? rule.Kind : "domain";

        var outbound = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var o in RoutingRule.Outbounds) outbound.Items.Add(o);
        outbound.SelectedItem = RoutingRule.Outbounds.Contains(rule.Outbound) ? rule.Outbound : "proxy";

        var isApp = rule.Kind == "process";
        var pattern = new TextBox
        {
            Text = rule.Pattern,
            PlaceholderText = isApp ? "choose an app, or type a process name (chrome.exe)" : "e.g. netflix.com · 1.1.1.1/32",
            Style = (Style)Application.Current.Resources["FieldTextBox"],
        };

        var browse = new Button
        {
            Style = (Style)Application.Current.Resources["SecondaryButton"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Visibility = isApp ? Visibility.Visible : Visibility.Collapsed,
        };
        var browseContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        browseContent.Children.Add(new SymbolIcon(Symbol.OpenFile) { Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentBase"] });
        browseContent.Children.Add(new TextBlock { Text = "choose app or shortcut" });
        browse.Content = browseContent;
        browse.Click += async (_, _) =>
        {
            var process = await PickAppProcessAsync();
            if (!string.IsNullOrWhiteSpace(process)) pattern.Text = process;
        };

        var hint = new TextBlock
        {
            Text = "domain = suffix match (covers subdomains). ip = CIDR. app = pick the .exe or .lnk; matched by process name.",
            Style = (Style)Application.Current.Resources["TextCaption"],
            TextWrapping = TextWrapping.Wrap,
        };

        kind.SelectionChanged += (_, _) =>
        {
            var appSelected = (kind.SelectedItem as string) == "process";
            browse.Visibility = appSelected ? Visibility.Visible : Visibility.Collapsed;
            pattern.PlaceholderText = appSelected ? "choose an app, or type a process name (chrome.exe)" : "e.g. netflix.com · 1.1.1.1/32";
        };

        var patternField = LabeledField("Pattern", pattern);
        patternField.Children.Add(browse);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(LabeledField("Match type", kind));
        panel.Children.Add(patternField);
        panel.Children.Add(LabeledField("Send to", outbound));
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "save",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        if (string.IsNullOrWhiteSpace(pattern.Text))
        {
            StatusText.Text = "pattern required";
            return false;
        }

        rule.Kind = kind.SelectedItem as string ?? "domain";
        rule.Pattern = pattern.Text.Trim();
        rule.Outbound = outbound.SelectedItem as string ?? "proxy";
        return true;
    }

    private async Task<string?> PickAppProcessAsync()
    {
        await Task.Yield();
        try
        {
            var path = NativeFilePicker.PickExecutableOrShortcut(WindowNative.GetWindowHandle(App.CurrentWindow));
            if (string.IsNullOrWhiteSpace(path)) return null;
            var process = ShortcutResolver.ToProcessName(path);
            StatusText.Text = $"app: {process}";
            return process;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"pick failed: {ex.Message}";
            return null;
        }
    }

    private static StackPanel LabeledField(string label, FrameworkElement control)
    {
        var container = new StackPanel { Spacing = 6 };
        container.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["FieldLabel"],
        });
        container.Children.Add(control);
        return container;
    }

    private async void OnRuleToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is not FrameworkElement { Tag: RoutingRule rule }) return;
        if (!_currentRules.Contains(rule)) return;
        if (_enabledSnapshot.TryGetValue(rule.Id, out var previous) && previous == rule.Enabled) return;
        _enabledSnapshot[rule.Id] = rule.Enabled;
        _vm.SaveRule(rule);
        StatusText.Text = $"{rule.Pattern}: {(rule.Enabled ? "on" : "off")} · {await _vm.ApplyAsync()}";
    }

    private async void OnDeleteRule(object sender, object e)
    {
        if (sender is not FrameworkElement { Tag: RoutingRule rule }) return;
        var dialog = new ContentDialog
        {
            Title = "Delete rule?",
            Content = $"{rule.KindLabel}  {rule.Pattern} → {rule.OutboundLabel}",
            PrimaryButtonText = "delete",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _vm.DeleteRule(rule);
        LoadAll();
        StatusText.Text = $"deleted: {rule.Pattern} · {await _vm.ApplyAsync()}";
    }

    private async void OnAddHop(object sender, object e)
    {
        var available = _vm.AvailableProfiles(_chainIds);
        if (available.Count == 0)
        {
            StatusText.Text = "no V2Ray configs to chain";
            return;
        }

        var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in available) picker.Items.Add(new ComboBoxItem { Content = $"{p.Name}  ·  {p.Protocol} {p.Address}", Tag = p.Id });
        picker.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(LabeledField("Add hop", picker));

        var dialog = new ContentDialog
        {
            Title = "Add chain hop",
            Content = panel,
            PrimaryButtonText = "add",
            CloseButtonText = "cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (picker.SelectedItem is not ComboBoxItem { Tag: long id }) return;

        _chainIds.Add(id);
        PersistChain();
    }

    private void OnHopUp(object sender, object e) => MoveHop(sender, -1);
    private void OnHopDown(object sender, object e) => MoveHop(sender, +1);

    private void MoveHop(object sender, int delta)
    {
        if (sender is not FrameworkElement { Tag: ChainHop hop }) return;
        var index = _chainIds.IndexOf(hop.ProfileId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _chainIds.Count) return;
        (_chainIds[index], _chainIds[target]) = (_chainIds[target], _chainIds[index]);
        PersistChain();
    }

    private void OnHopRemove(object sender, object e)
    {
        if (sender is not FrameworkElement { Tag: ChainHop hop }) return;
        _chainIds.RemoveAll(id => id == hop.ProfileId);
        PersistChain();
    }

    private async void OnClearChain(object sender, object e)
    {
        _chainIds.Clear();
        _vm.ClearChain();
        RenderChain();
        StatusText.Text = $"chain cleared · {await _vm.ApplyAsync()}";
    }

    private async void PersistChain()
    {
        _vm.SaveChain(_chainIds);
        RenderChain();
        var summary = _chainIds.Count == 0 ? "chain cleared" : $"chain: {_chainIds.Count} hop(s)";
        StatusText.Text = $"{summary} · {await _vm.ApplyAsync()}";
    }
}
