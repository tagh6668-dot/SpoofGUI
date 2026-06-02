using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Engine;
using SpoofGUI.Models;

namespace SpoofGUI.GUI.ViewModels;

public sealed class V2RayPageViewModel
{
    private readonly V2RayProfileRepository _profiles;
    private readonly XrayCoreService _xray;
    private readonly SingBoxTunnelService _tunnel;
    private readonly ProxyPortSettings _ports;
    private readonly AppSettings _appSettings;
    private readonly ConnectionGuard _guard;

    public V2RayPageViewModel(V2RayProfileRepository profiles, XrayCoreService xray, SingBoxTunnelService tunnel, ProxyPortSettings ports, AppSettings appSettings, ConnectionGuard guard)
    {
        _profiles = profiles;
        _xray = xray;
        _tunnel = tunnel;
        _ports = ports;
        _appSettings = appSettings;
        _guard = guard;
    }

    public void ArmKillSwitch() => _guard.ArmV2Ray();
    public void DisarmKillSwitch() => _guard.DisarmV2Ray();

    public int V2RayModeIndex => _appSettings.V2RayMode switch
    {
        "Tunnel" => 1,
        "SystemProxy" => 2,
        _ => 0,
    };

    public void SetMode(string mode) => _appSettings.V2RayMode = mode;

    public bool TunnelRunning => _tunnel.IsRunning;

    public Task StartTunnelAsync(V2RayProfile profile) => Task.Run(() => _tunnel.Start(profile));
    public Task StopTunnelAsync() => Task.Run(() => _tunnel.Stop());

    public int SocksPort => _ports.SocksPort;
    public int HttpPort => _ports.HttpPort;

    public string SystemProxyEndpoint =>
        $"http=127.0.0.1:{_ports.HttpPort};https=127.0.0.1:{_ports.HttpPort};socks=127.0.0.1:{_ports.SocksPort}";

    public IReadOnlyList<V2RayProfile> LoadProfiles() => _profiles.All();
    public bool IsRunning => _xray.IsRunning;
    public Task<bool> RefreshRunningAsync() => _xray.RefreshRunningAsync();
    public Task<string> CoreVersionAsync() => _xray.VersionAsync();

    public ImportResult ImportMany(string text, string mode)
    {
        var imported = new List<V2RayProfile>();
        var failed = 0;
        foreach (var entry in V2RayConfigParser.SplitConfigs(text))
        {
            try
            {
                var profile = V2RayConfigParser.Parse(entry);
                profile.Mode = mode;
                _profiles.Upsert(profile);
                imported.Add(profile);
            }
            catch
            {
                failed++;
            }
        }

        return new ImportResult(imported, failed);
    }

    public void Save(V2RayProfile profile) => _profiles.Upsert(profile);
    public void Delete(V2RayProfile profile) => _profiles.Delete(profile.Id);
    public Task StartAsync(V2RayProfile profile) => _xray.StartAsync(profile);
    public Task StopAsync() => _xray.StopAsync();
    public Task<long> TestRealDelayAsync(V2RayProfile profile, CancellationToken ct = default) => _xray.TestRealDelayAsync(profile, ct);
    public void RememberPing(long id, string ping) => _profiles.RememberPing(id, ping);
}

public sealed record ImportResult(IReadOnlyList<V2RayProfile> Imported, int Failed);

internal static class V2RayConfigParser
{
    private static readonly Regex ConfigScheme = new(
        @"(?:vless|vmess|trojan|ssr|ss|socks5|socks|https|http|hysteria2|hysteria|hy2|tuic|wireguard|warp|naive|brook)://",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<string> SplitConfigs(string text)
    {
        var matches = ConfigScheme.Matches(text);
        if (matches.Count == 0)
        {
            var single = text.Trim();
            return single.Length > 0 ? new List<string> { single } : new List<string>();
        }

        var configs = new List<string>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var entry = text[start..end].Trim();
            if (entry.Length > 0) configs.Add(entry);
        }

        return configs;
    }

    public static V2RayProfile Parse(string text)
    {
        var raw = text.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return new V2RayProfile { Name = "custom config", Protocol = "custom", RawUri = raw };
        }

        return uri.Scheme.ToLowerInvariant() switch
        {
            "vless" => ParseCommonUri(uri, raw, "vless"),
            "trojan" => ParseCommonUri(uri, raw, "trojan"),
            "vmess" => ParseVmess(raw),
            "ss" => ParseShadowsocks(uri, raw),
            _ => ParseCommonUri(uri, raw, uri.Scheme.ToLowerInvariant()),
        };
    }

    private static V2RayProfile ParseCommonUri(Uri uri, string raw, string protocol)
    {
        var query = ParseQuery(uri.Query);
        var name = string.IsNullOrWhiteSpace(uri.Fragment)
            ? $"{protocol} {uri.Host}"
            : WebUtility.UrlDecode(uri.Fragment.TrimStart('#'));

        return new V2RayProfile
        {
            Name = name,
            Protocol = protocol,
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            UserId = WebUtility.UrlDecode(uri.UserInfo),
            Security = query.GetValueOrDefault("security") ?? query.GetValueOrDefault("type") ?? "",
            Transport = query.GetValueOrDefault("type") ?? "tcp",
            ServerName = query.GetValueOrDefault("sni") ?? query.GetValueOrDefault("host") ?? "",
            RawUri = raw,
        };
    }

    private static V2RayProfile ParseVmess(string raw)
    {
        var payload = raw["vmess://".Length..].Trim();
        var json = DecodeBase64Url(payload);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new V2RayProfile
        {
            Name = GetString(root, "ps", "vmess config"),
            Protocol = "vmess",
            Address = GetString(root, "add", ""),
            Port = int.TryParse(GetString(root, "port", "443"), out var port) ? port : 443,
            UserId = GetString(root, "id", ""),
            Security = GetString(root, "tls", ""),
            Transport = GetString(root, "net", "tcp"),
            ServerName = GetString(root, "sni", GetString(root, "host", "")),
            RawUri = raw,
        };
    }

    private static V2RayProfile ParseShadowsocks(Uri uri, string raw)
    {
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            var payload = raw["ss://".Length..].Split('#', 2)[0].Split('?', 2)[0];
            var decoded = DecodeBase64Url(payload);
            if (Uri.TryCreate($"ss://{decoded}", UriKind.Absolute, out var decodedUri))
            {
                return ParseShadowsocks(decodedUri, raw);
            }
        }

        var name = string.IsNullOrWhiteSpace(uri.Fragment)
            ? $"ss {uri.Host}"
            : WebUtility.UrlDecode(uri.Fragment.TrimStart('#'));

        return new V2RayProfile
        {
            Name = name,
            Protocol = "ss",
            Address = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 443,
            UserId = WebUtility.UrlDecode(uri.UserInfo),
            RawUri = raw,
        };
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                result[WebUtility.UrlDecode(parts[0])] = WebUtility.UrlDecode(parts[1]);
            }
        }

        return result;
    }

    private static string DecodeBase64Url(string value)
    {
        value = value.Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private static string GetString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
}
