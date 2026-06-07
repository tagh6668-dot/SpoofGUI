using System.ComponentModel;

namespace SpoofGUI.Models;

public sealed class RoutingRule : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Kind { get; set; } = "domain";
    public string Pattern { get; set; } = "";
    public string Outbound { get; set; } = "proxy";
    public int SortOrder { get; set; }

    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    public string KindLabel => Kind switch
    {
        "process" => "app",
        "ip" => "ip",
        _ => "domain",
    };

    public string OutboundLabel => Outbound switch
    {
        "direct" => "direct",
        "block" => "block",
        _ => "proxy",
    };

    public static readonly string[] Kinds = ["domain", "ip", "process"];
    public static readonly string[] Outbounds = ["proxy", "direct", "block"];

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChainHop
{
    public long ProfileId { get; init; }
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Address { get; init; } = "";
    public string Summary => $"{Protocol} · {Address}";
}
