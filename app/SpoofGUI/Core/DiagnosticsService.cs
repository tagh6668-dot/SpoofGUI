using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SpoofGUI.Database;
using SpoofGUI.Engine;

namespace SpoofGUI.Core;

public sealed class DiagnosticsService
{
    private readonly ProxyPortSettings _ports;
    private readonly AppSettings _settings;
    private readonly ProfileRepository _profiles;
    private readonly V2RayProfileRepository _v2ray;
    private readonly XrayCoreService _xray;
    private readonly SingBoxTunnelService _tunnel;
    private readonly EngineSupervisor _engine;

    public DiagnosticsService(
        ProxyPortSettings ports,
        AppSettings settings,
        ProfileRepository profiles,
        V2RayProfileRepository v2ray,
        XrayCoreService xray,
        SingBoxTunnelService tunnel,
        EngineSupervisor engine)
    {
        _ports = ports;
        _settings = settings;
        _profiles = profiles;
        _v2ray = v2ray;
        _xray = xray;
        _tunnel = tunnel;
        _engine = engine;
    }

    public string WinDivertStatus()
    {
        var dll = Path.Combine(WinDivert.EngineDirectory, "WinDivert.dll");
        var sys = Path.Combine(WinDivert.EngineDirectory, WinDivert.RequiredDriverName);
        return WinDivert.IsAvailable()
            ? $"ready: WinDivert.dll + {WinDivert.RequiredDriverName}"
            : $"missing: dll={File.Exists(dll)}, {WinDivert.RequiredDriverName}={File.Exists(sys)}";
    }

    public string BuildReport()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        var endpoint = $"http=127.0.0.1:{_ports.HttpPort};https=127.0.0.1:{_ports.HttpPort};socks=127.0.0.1:{_ports.SocksPort}";
        var sb = new StringBuilder();
        sb.AppendLine("SpoofGUI diagnostics");
        sb.AppendLine($"time: {DateTimeOffset.Now:O}");
        sb.AppendLine($"version: {version}");
        sb.AppendLine($"process_arch: {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"os_arch: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"base_dir: {AppContext.BaseDirectory}");
        sb.AppendLine($"data_dir: {Paths.AppDataDir}");
        sb.AppendLine();
        sb.AppendLine("files");
        sb.AppendLine($"xray: {File.Exists(Paths.XrayExePath)} {Paths.XrayExePath}");
        sb.AppendLine($"sing-box: {File.Exists(Paths.SingBoxExePath)} {Paths.SingBoxExePath}");
        sb.AppendLine($"windivert: {WinDivertStatus()}");
        sb.AppendLine();
        sb.AppendLine("runtime");
        sb.AppendLine($"sni_engine_running: {_engine.IsRunning}");
        sb.AppendLine($"xray_running: {_xray.IsRunning}");
        sb.AppendLine($"singbox_tunnel_running: {_tunnel.IsRunning}");
        sb.AppendLine($"v2ray_mode: {_settings.V2RayMode}");
        sb.AppendLine($"socks_port: {_ports.SocksPort}");
        sb.AppendLine($"http_port: {_ports.HttpPort}");
        sb.AppendLine($"system_proxy_enabled: {SystemProxy.IsEnabled()}");
        sb.AppendLine($"system_proxy_server: {SystemProxy.GetProxyServer() ?? ""}");
        sb.AppendLine($"system_proxy_expected: {endpoint}");
        sb.AppendLine();
        sb.AppendLine("profiles");
        sb.AppendLine($"sni_profiles: {_profiles.All().Count}");
        sb.AppendLine($"v2ray_configs: {_v2ray.All().Count}");
        sb.AppendLine();
        sb.AppendLine("recent_logs");
        foreach (var line in AppLog.Snapshot().TakeLast(120))
            sb.AppendLine(line);
        return sb.ToString();
    }

    public IReadOnlyList<HealthCheckItem> HealthChecks()
    {
        var endpoint = $"http=127.0.0.1:{_ports.HttpPort};https=127.0.0.1:{_ports.HttpPort};socks=127.0.0.1:{_ports.SocksPort}";
        var appExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        var checks = new List<HealthCheckItem>
        {
            Check(appExe is not null, "app executable", appExe ?? "unknown"),
            Check(IsWritable(Paths.AppDataDir), "app data writable", Paths.AppDataDir),
            Check(File.Exists(Paths.XrayExePath), "xray bundled", Paths.XrayExePath),
            Check(File.Exists(Paths.SingBoxExePath), "sing-box bundled", Paths.SingBoxExePath),
            Check(WinDivert.IsAvailable(), "WinDivert ready", WinDivertStatus()),
            Check(!SystemProxy.LooksLikeSpoofGuiProxy(_ports) || _xray.IsRunning || _tunnel.IsRunning, "system proxy clean", SystemProxy.GetProxyServer() ?? "disabled"),
            Check(_profiles.All().Count > 0, "SNI profile exists", $"{_profiles.All().Count} profile(s)"),
            Check(_v2ray.All().Count > 0, "V2Ray config exists", $"{_v2ray.All().Count} config(s)"),
            Check(_ports.SocksPort != _ports.HttpPort, "proxy ports valid", endpoint),
        };
        return checks;
    }

    public string BuildHealthText()
    {
        var sb = new StringBuilder();
        foreach (var item in HealthChecks())
            sb.AppendLine($"{item.Status.ToUpperInvariant(),-6} {item.Name}: {item.Detail}");
        return sb.ToString();
    }

    public string PortableReadiness()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var portable = !baseDir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)
            && !baseDir.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase);
        var writable = IsWritable(baseDir);
        return portable ? $"portable: {(writable ? "ready" : "folder not writable")}" : "installed mode";
    }

    private static HealthCheckItem Check(bool ok, string name, string detail) =>
        new(ok ? "ok" : "warn", name, detail);

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ".spoofgui-write-test");
            File.WriteAllText(path, "ok");
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }
}

public sealed record HealthCheckItem(string Status, string Name, string Detail);
