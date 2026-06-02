using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpoofGUI.Core;
using SpoofGUI.Database;

namespace SpoofGUI.GUI.Pages;

public sealed partial class ConnectionsPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ConnectionsPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        try
        {
            var ports = App.Services.GetRequiredService<ProxyPortSettings>();
            var active = App.Services.GetRequiredService<ProfileRepository>().GetActive();
            var watched = new List<int> { active?.ListenPort ?? 40443, ports.SocksPort, ports.HttpPort };

            var conns = NetStats.ActiveConnections(watched);
            ConnList.ItemsSource = conns;

            var established = conns.Count(c => c.State == "Established");
            ConnSummary.Text = conns.Count == 0
                ? $"no active sessions  ·  watching ports {string.Join(", ", watched.Distinct())}"
                : $"{established} established · {conns.Count} total  ·  ports {string.Join(", ", watched.Distinct())}";
        }
        catch (Exception e)
        {
            ConnSummary.Text = $"unavailable: {e.Message}";
        }
    }
}
