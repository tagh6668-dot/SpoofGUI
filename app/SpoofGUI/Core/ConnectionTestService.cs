using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SpoofGUI.Core;

public sealed record ConnectionTestResult(
    string DirectEgress,
    string ProxiedEgress,
    bool ProxyReachable,
    bool EgressChanged,
    string DnsServers,
    string DnsNote,
    string Status);

public sealed class ConnectionTestService
{
    private const string GeoEndpoint = "http://ip-api.com/json/?fields=status,country,query";

    private readonly ProxyPortSettings _ports;
    private readonly AppSettings _appSettings;

    public ConnectionTestService(ProxyPortSettings ports, AppSettings appSettings)
    {
        _ports = ports;
        _appSettings = appSettings;
    }

    public async Task<ConnectionTestResult> RunAsync(CancellationToken ct = default)
    {
        var tunnel = string.Equals(_appSettings.V2RayMode, "Tunnel", StringComparison.OrdinalIgnoreCase);

        var direct = await FetchEgressAsync(null, ct);
        var proxy = await FetchEgressAsync(new WebProxy($"http://127.0.0.1:{_ports.HttpPort}"), ct);

        var proxyReachable = proxy.Ok;
        var directText = direct.Ok ? direct.Describe() : "unavailable";
        var proxiedText = proxyReachable
            ? proxy.Describe()
            : tunnel ? "n/a (tunnel routes all traffic)" : "local proxy not reachable";

        var egressChanged = proxyReachable && direct.Ok && proxy.Ip != direct.Ip;

        var dnsServers = ActiveDnsServers();
        var dnsNote = BuildDnsNote(dnsServers, tunnel);

        string status;
        if (tunnel)
            status = direct.Ok ? $"tunnel egress: {direct.Describe()}" : "tunnel egress unavailable";
        else if (proxyReachable)
            status = egressChanged
                ? "proxy active — egress IP differs from direct"
                : "proxy reachable but egress IP matches direct (check routing)";
        else
            status = "no local proxy running — showing direct connection";

        return new ConnectionTestResult(
            directText,
            proxiedText,
            proxyReachable,
            egressChanged,
            string.Join(", ", dnsServers),
            dnsNote,
            status);
    }

    private static async Task<EgressInfo> FetchEgressAsync(WebProxy? proxy, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                UseProxy = proxy is not null,
                Proxy = proxy,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SpoofGUI/1.0");
            var json = await client.GetStringAsync(GeoEndpoint, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ip = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var country = root.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
            return string.IsNullOrEmpty(ip) ? EgressInfo.Failed : new EgressInfo(true, ip, country);
        }
        catch
        {
            return EgressInfo.Failed;
        }
    }

    private static IReadOnlyList<string> ActiveDnsServers()
    {
        var servers = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var dns in nic.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = dns.ToString();
                    if (!servers.Contains(text)) servers.Add(text);
                }
            }
        }
        catch { }
        return servers.Count > 0 ? servers : new List<string> { "unknown" };
    }

    private static string BuildDnsNote(IReadOnlyList<string> servers, bool tunnel)
    {
        if (tunnel) return "Tunnel mode hijacks DNS through sing-box — leaks unlikely.";
        var localResolver = servers.Any(IsPrivate);
        return localResolver
            ? "Resolver is on your local network (router/ISP). DNS likely not proxied — use Tunnel mode to prevent leaks."
            : "Resolver is a public DNS. Verify it matches your intended provider.";
    }

    private static bool IsPrivate(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        var b = addr.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || b[0] == 127;
    }

    private readonly record struct EgressInfo(bool Ok, string Ip, string Country)
    {
        public static readonly EgressInfo Failed = new(false, "", "");
        public string Describe() => string.IsNullOrEmpty(Country) ? Ip : $"{Ip} · {Country}";
    }
}
