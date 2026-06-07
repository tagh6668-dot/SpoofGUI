using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpoofGUI.Models;

namespace SpoofGUI.Core;

public static partial class V2RayConfigParser
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

    public static IReadOnlyList<V2RayProfile> ParseSubscriptionBody(string body)
    {
        var text = NormalizeSubscriptionBody(body);
        var profiles = new List<V2RayProfile>();
        foreach (var entry in SplitConfigs(text))
        {
            try
            {
                var profile = Parse(entry);
                if (!string.IsNullOrWhiteSpace(profile.Address) || profile.Protocol == "custom")
                    profiles.Add(profile);
            }
            catch { }
        }
        return profiles;
    }

    private static string NormalizeSubscriptionBody(string body)
    {
        var trimmed = (body ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (ConfigScheme.IsMatch(trimmed)) return trimmed;
        var decoded = TryDecodeBase64(trimmed);
        return decoded is not null && ConfigScheme.IsMatch(decoded) ? decoded : trimmed;
    }

    private static string? TryDecodeBase64(string value)
    {
        try
        {
            var cleaned = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return DecodeBase64Url(cleaned);
        }
        catch { return null; }
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
