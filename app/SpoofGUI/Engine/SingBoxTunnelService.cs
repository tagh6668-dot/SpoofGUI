using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Models;

namespace SpoofGUI.Engine;

public sealed class SingBoxTunnelService : IDisposable
{
    private const string TunDeviceName = "SpoofGUI-Tunnel";
    private const string TunAddress = "198.18.0.1/30";

    private readonly ILogger<SingBoxTunnelService> _log;
    private readonly AppSettings _appSettings;
    private readonly RoutingRuleRepository _rules;
    private readonly V2RayProfileRepository _v2rayProfiles;
    private readonly ProfileRepository _sniProfiles;
    private Process? _proc;
    private V2RayProfile? _active;

    public bool IsRunning => _proc is { HasExited: false };

    public SingBoxTunnelService(ILogger<SingBoxTunnelService> log, AppSettings appSettings, RoutingRuleRepository rules, V2RayProfileRepository v2rayProfiles, ProfileRepository sniProfiles)
    {
        _log = log;
        _appSettings = appSettings;
        _rules = rules;
        _v2rayProfiles = v2rayProfiles;
        _sniProfiles = sniProfiles;
    }

    public void Start(V2RayProfile profile)
    {
        if (IsRunning) return;
        if (!File.Exists(Paths.SingBoxExePath))
            throw new FileNotFoundException($"sing-box not found: {Paths.SingBoxExePath}");

        _active = profile;
        KillOrphaned();

        var config = BuildConfig(profile).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.SingBoxConfigPath)!);
        File.WriteAllText(Paths.SingBoxConfigPath, config);
        AppLog.Info("sing-box tun config written");

        var psi = new ProcessStartInfo
        {
            FileName = Paths.SingBoxExePath,
            Arguments = $"run -c \"{Paths.SingBoxConfigPath}\"",
            WorkingDirectory = Path.GetDirectoryName(Paths.SingBoxExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start sing-box");
        _proc.EnableRaisingEvents = true;
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Warn($"sing-box: {e.Data}"); };
        _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) AppLog.Info($"sing-box: {e.Data}"); };
        _proc.BeginErrorReadLine();
        _proc.BeginOutputReadLine();
        AppLog.Info($"sing-box tun started pid={_proc.Id}");

        if (_proc.WaitForExit(1500))
            throw new InvalidOperationException($"sing-box exited immediately (code {_proc.ExitCode}); check Logs");
    }

    public void Stop()
    {
        if (_proc is not null)
        {
            try
            {
                if (!_proc.HasExited)
                {

                    TryGracefulStop(_proc.Id);
                    if (!_proc.WaitForExit(4000))
                    {
                        _proc.Kill(entireProcessTree: true);
                        _proc.WaitForExit(3000);
                    }
                }
            }
            catch (Exception e) { _log.LogWarning(e, "sing-box stop"); }
            _proc.Dispose();
            _proc = null;
        }

        try { RunShell("ipconfig /flushdns"); } catch { }
        AppLog.Info("sing-box tun stopped");
    }

    public void Dispose() => Stop();

    public bool Reload()
    {
        if (_active is null || !IsRunning) return false;
        var profile = _active;
        Stop();
        Start(profile);
        AppLog.Info("sing-box reloaded (routing changed)");
        return true;
    }

    private static void TryGracefulStop(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill", $"/T /PID {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
        }
        catch { }
    }

    private static void KillOrphaned()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { }
            }
        }
        catch { }
    }

    private static void RunShell(string command)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(5000);
    }

    private JsonObject BuildConfig(V2RayProfile profile)
    {
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },

            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    DnsServer("remote", _appSettings.RemoteDns, "proxy"),
                    DnsServer("local", _appSettings.DirectDns, null),
                    DnsServer("bootstrap", _appSettings.BootstrapDns, null),
                },
                ["final"] = "remote",
                ["strategy"] = _appSettings.DnsStrategy,
            },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunDeviceName,
                    ["address"] = new JsonArray { TunAddress },
                    ["mtu"] = 1500,
                    ["auto_route"] = true,

                    ["strict_route"] = false,
                    ["stack"] = "gvisor",
                },
            },
            ["outbounds"] = BuildOutbounds(profile),
            ["route"] = new JsonObject
            {

                ["rules"] = BuildRouteRules(profile),
                ["auto_detect_interface"] = true,

                ["default_domain_resolver"] = new JsonObject { ["server"] = "bootstrap" },
                ["final"] = "proxy",
            },
        };
    }

    private JsonArray BuildOutbounds(V2RayProfile profile)
    {
        var hops = ResolveChain(profile);
        var arr = new JsonArray();
        for (var i = 0; i < hops.Count; i++)
        {
            var tag = i == 0 ? "proxy" : $"hop{i}";
            var ob = BuildOutbound(hops[i], tag);
            if (i < hops.Count - 1) ob["detour"] = $"hop{i + 1}";
            arr.Add(ob);
        }
        arr.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });
        return arr;
    }

    private List<string> BypassIps(V2RayProfile profile)
    {
        var ips = new List<string>();
        void Add(string? value)
        {
            value = (value ?? "").Trim();
            if (System.Net.IPAddress.TryParse(value, out var addr)
                && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !System.Net.IPAddress.IsLoopback(addr)
                && !ips.Contains(value))
            {
                ips.Add(value);
            }
        }

        Add(_sniProfiles.GetActive()?.ConnectIp);
        Add(profile.Address);
        Add(NormalizeServerAddress(profile.Address));
        foreach (var hop in ResolveChain(profile)) Add(NormalizeServerAddress(hop.Address));
        return ips;
    }

    private List<V2RayProfile> ResolveChain(V2RayProfile fallback)
    {
        var ids = _appSettings.ProxyChain;
        if (ids.Count == 0) return [fallback];
        var all = _v2rayProfiles.All().ToDictionary(p => p.Id);
        var hops = new List<V2RayProfile>();
        foreach (var id in ids)
            if (all.TryGetValue(id, out var p)) hops.Add(p);
        if (hops.Count == 0) return [fallback];
        AppLog.Info($"sing-box proxy chain: {hops.Count} hop(s) — entry {hops[0].Name}");
        return hops;
    }

    private JsonArray BuildRouteRules(V2RayProfile profile)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject
            {
                ["ip_cidr"] = new JsonArray
                {
                    "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16",
                    "169.254.0.0/16", "100.64.0.0/10", "224.0.0.0/4",
                },
                ["action"] = "route",
                ["outbound"] = "direct",
            },
        };

        var bypass = BypassIps(profile);
        if (bypass.Count > 0)
        {
            var cidrs = new JsonArray();
            foreach (var ip in bypass) cidrs.Add($"{ip}/32");
            rules.Add(new JsonObject
            {
                ["ip_cidr"] = cidrs,
                ["action"] = "route",
                ["outbound"] = "direct",
            });
            AppLog.Info($"sing-box: bypass tunnel (direct) for {string.Join(", ", bypass)}");
        }

        foreach (var rule in _rules.Enabled())
        {
            var pattern = (rule.Pattern ?? "").Trim();
            if (pattern.Length == 0) continue;
            var node = new JsonObject();
            switch (rule.Kind)
            {
                case "process":
                    node["process_name"] = new JsonArray { pattern };
                    break;
                case "ip":
                    node["ip_cidr"] = new JsonArray { pattern };
                    break;
                default:
                    node["domain_suffix"] = new JsonArray { pattern.TrimStart('.') };
                    break;
            }
            if (rule.Outbound == "block")
            {
                node["action"] = "reject";
            }
            else
            {
                node["action"] = "route";
                node["outbound"] = rule.Outbound == "direct" ? "direct" : "proxy";
            }
            rules.Add(node);
        }
        return rules;
    }

    private static JsonObject DnsServer(string tag, string value, string? detour)
    {
        value = (value ?? "").Trim();
        var node = new JsonObject { ["tag"] = tag };
        var schemeIndex = value.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0)
        {
            var scheme = value.Substring(0, schemeIndex).ToLowerInvariant();
            var rest = value.Substring(schemeIndex + 3);
            var slash = rest.IndexOf('/');
            var host = slash >= 0 ? rest.Substring(0, slash) : rest;
            var path = slash >= 0 ? rest.Substring(slash) : null;
            node["type"] = scheme switch
            {
                "https" => "https",
                "tls" => "tls",
                "quic" => "quic",
                "h3" => "h3",
                _ => "udp",
            };
            node["server"] = host;
            if (path is not null && scheme is "https" or "h3") node["path"] = path;
        }
        else
        {
            node["type"] = "udp";
            node["server"] = value;
        }

        if (detour is not null) node["detour"] = detour;
        return node;
    }

    private JsonObject BuildOutbound(V2RayProfile profile, string tag)
    {
        var proto = profile.Protocol.ToLowerInvariant();
        var query = ParseQuery(profile.RawUri);
        var serverAddress = NormalizeServerAddress(profile.Address);
        var outbound = new JsonObject
        {
            ["type"] = proto,
            ["tag"] = tag,
            ["server"] = serverAddress,
            ["server_port"] = profile.Port,
        };

        switch (proto)
        {
            case "vless":
                outbound["uuid"] = profile.UserId;
                if (query.TryGetValue("flow", out var flow) && !string.IsNullOrWhiteSpace(flow))
                    outbound["flow"] = flow;
                break;
            case "vmess":
                outbound["uuid"] = profile.UserId;
                outbound["security"] = "auto";
                outbound["alter_id"] = 0;
                break;
            case "trojan":
                outbound["password"] = profile.UserId;
                break;
            case "shadowsocks":
            case "ss":
                outbound["type"] = "shadowsocks";
                var parts = profile.UserId.Split(new[] { ':' }, 2);
                outbound["method"] = parts.Length == 2 ? parts[0] : "aes-128-gcm";
                outbound["password"] = parts.Length == 2 ? parts[1] : profile.UserId;
                break;
            default:
                throw new InvalidOperationException(
                    $"Tunnel mode (sing-box) does not support protocol '{profile.Protocol}'. Use Proxy or System Proxy mode.");
        }

        AddTransportAndTls(outbound, profile, query, serverAddress);
        return outbound;
    }

    private static void AddTransportAndTls(JsonObject outbound, V2RayProfile profile, Dictionary<string, string> query, string serverAddress)
    {
        var network = string.IsNullOrWhiteSpace(profile.Transport) ? "tcp" : profile.Transport.ToLowerInvariant();
        var security = profile.Security.ToLowerInvariant();
        var serverName = string.IsNullOrWhiteSpace(profile.ServerName) ? serverAddress : profile.ServerName;

        if (security is "tls" or "reality")
        {
            var tls = new JsonObject
            {
                ["enabled"] = true,
                ["server_name"] = serverName,
                ["insecure"] = false,
                ["utls"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["fingerprint"] = query.GetValueOrDefault("fp", "chrome"),
                },
            };
            if (security == "reality")
            {
                tls["reality"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["public_key"] = query.GetValueOrDefault("pbk", ""),
                    ["short_id"] = query.GetValueOrDefault("sid", ""),
                };
            }
            outbound["tls"] = tls;
        }

        if (network == "ws")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"] = "ws",
                ["path"] = query.GetValueOrDefault("path", "/"),
                ["headers"] = new JsonObject { ["Host"] = query.GetValueOrDefault("host", serverName) },
            };
        }
        else if (network == "grpc")
        {
            outbound["transport"] = new JsonObject
            {
                ["type"] = "grpc",
                ["service_name"] = query.GetValueOrDefault("serviceName", ""),
            };
        }
    }

    private static Dictionary<string, string> ParseQuery(string rawUri)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = rawUri.IndexOf('?');
        if (q < 0) return map;
        var query = rawUri.Substring(q + 1);
        var frag = query.IndexOf('#');
        if (frag >= 0) query = query.Substring(0, frag);
        foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var key = WebUtility.UrlDecode(pair.Substring(0, idx));
            var val = WebUtility.UrlDecode(pair.Substring(idx + 1));
            if (!string.IsNullOrWhiteSpace(key)) map[key] = val;
        }
        return map;
    }

    private string NormalizeServerAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ip) || !IPAddress.IsLoopback(ip))
            return address;

        var spoof = _sniProfiles.GetActive();
        var replacement = spoof is null ? null : ResolveInterfaceIPv4(spoof.ConnectIp, spoof.ConnectPort);
        if (string.IsNullOrWhiteSpace(replacement) || IPAddress.IsLoopback(IPAddress.Parse(replacement)))
            replacement = FirstNonLoopbackIPv4();

        if (!string.IsNullOrWhiteSpace(replacement))
        {
            AppLog.Info($"sing-box: rewrote loopback target {address} to {replacement} because local loopback TCP is unavailable");
            return replacement;
        }

        return address;
    }

    private static string? ResolveInterfaceIPv4(string remoteIp, int remotePort)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect(IPAddress.Parse(remoteIp), remotePort);
            var ip = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
            if (!string.IsNullOrWhiteSpace(ip) && !IPAddress.IsLoopback(IPAddress.Parse(ip)))
                return ip;

            return NetworkHelper.GetLocalPhysicalIPAddress();
        }
        catch
        {
            return NetworkHelper.GetLocalPhysicalIPAddress();
        }
    }

    private static string? FirstNonLoopbackIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }

        return null;
    }
}
