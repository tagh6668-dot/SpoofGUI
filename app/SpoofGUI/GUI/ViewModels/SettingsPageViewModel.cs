using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SpoofGUI.Core;
using SpoofGUI.Database;

namespace SpoofGUI.GUI.ViewModels;

public sealed class SettingsPageViewModel
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/ZethRise/SpoofGUI/releases?per_page=10";
    public const string ReleasesPageUrl = "https://github.com/ZethRise/SpoofGUI/releases";

    private readonly SettingsRepository _settings;
    private readonly ProxyPortSettings _ports;
    private readonly AppSettings _app;
    private readonly ILogger<SettingsPageViewModel> _log;

    public SettingsPageViewModel(SettingsRepository settings, ProxyPortSettings ports, AppSettings app, ILogger<SettingsPageViewModel> log)
    {
        _settings = settings;
        _ports = ports;
        _app = app;
        _log = log;
    }

    public int SocksPort => _ports.SocksPort;
    public int HttpPort => _ports.HttpPort;

    public bool XrayAllowInsecure
    {
        get => _app.XrayAllowInsecure;
        set => _app.XrayAllowInsecure = value;
    }

    public string XrayLogLevel
    {
        get => _app.XrayLogLevel;
        set => _app.XrayLogLevel = value;
    }

    public string V2RayMode
    {
        get => _app.V2RayMode;
        set => _app.V2RayMode = value;
    }

    public bool CheckUpdatesOnLaunch
    {
        get => _app.CheckUpdatesOnLaunch;
        set => _app.CheckUpdatesOnLaunch = value;
    }

    public bool FastMode
    {
        get => _app.FastMode;
        set => _app.FastMode = value;
    }

    public bool KillSwitch
    {
        get => _app.KillSwitch;
        set => _app.KillSwitch = value;
    }

    public string RemoteDns { get => _app.RemoteDns; set => _app.RemoteDns = value; }
    public string DirectDns { get => _app.DirectDns; set => _app.DirectDns = value; }
    public string BootstrapDns { get => _app.BootstrapDns; set => _app.BootstrapDns = value; }
    public string DnsStrategy { get => _app.DnsStrategy; set => _app.DnsStrategy = value; }

    public void SaveDns(string remote, string direct, string bootstrap)
    {
        _app.RemoteDns = remote;
        _app.DirectDns = direct;
        _app.BootstrapDns = bootstrap;
    }

    public string DataFolder => Paths.AppDataDir;

    public void OpenDataFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Paths.AppDataDir,
            UseShellExecute = true,
        });
    }

    public string ResetPorts()
    {
        _ports.Set(ProxyPortSettings.DefaultSocksPort, ProxyPortSettings.DefaultHttpPort);
        return $"reset: socks {ProxyPortSettings.DefaultSocksPort}, http {ProxyPortSettings.DefaultHttpPort}";
    }

        public string? SavePorts(string socksText, string httpText)
    {
        if (!int.TryParse(socksText?.Trim(), out var socks))
            return "SOCKS port must be a number";
        if (!int.TryParse(httpText?.Trim(), out var http))
            return "HTTP port must be a number";
        try
        {
            _ports.Set(socks, http);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public string Theme
    {
        get => _settings.Get("theme") ?? "dark";
    }

    public void SetTheme(string t) => _settings.Set("theme", t);

    public string LastUpdateCheckText()
    {
        var ts = _settings.Get("last_update_check");
        return string.IsNullOrEmpty(ts) ? "never checked" : $"last check: {ts}";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var url = _settings.Get("update_repo_url") ?? ReleasesApiUrl;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpoofGUI");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url));
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _settings.Set("last_update_check", now);

            var release = GetLatestPublishedRelease(doc.RootElement);
            if (release is null)
            {
                return new UpdateCheckResult("no SpoofGUI releases published yet", $"last check: {now}", ReleasesPageUrl, false);
            }

            var tag = release.Value.GetProperty("tag_name").GetString() ?? "?";
            var releaseUrl = release.Value.TryGetProperty("html_url", out var html)
                ? html.GetString() ?? ReleasesPageUrl
                : ReleasesPageUrl;

            var installed = GetInstalledVersion();
            var latest = TryParseVersion(tag);
            if (latest is null)
            {
                return new UpdateCheckResult($"latest SpoofGUI: {tag}", $"last check: {now}", releaseUrl, false);
            }

            var isNewer = latest > installed;
            var status = isNewer
                ? $"update available: {tag} (installed: {installed.ToString(3)})"
                : $"up to date: {installed.ToString(3)} (latest: {tag})";

            return new UpdateCheckResult(status, $"last check: {now}", releaseUrl, isNewer);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _log.LogWarning(e, "update channel not found");
            return new UpdateCheckResult(
                "release channel unavailable: repo is private, missing, or not published",
                LastUpdateCheckText(),
                ReleasesPageUrl,
                false);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "update check failed");
            return new UpdateCheckResult($"check failed: {e.Message}", LastUpdateCheckText(), ReleasesPageUrl, false);
        }
    }

    public async Task<string> DownloadAndInstallLatestAsync()
    {
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.X86 ? "x86" : "amd64";
        var assetName = $"SpoofGUI-Setup-{arch}.exe";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SpoofGUI");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesApiUrl));
            var release = GetLatestPublishedRelease(doc.RootElement);
            if (release is null) return "no published release to install";

            string? url = null;
            if (release.Value.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() == assetName)
                    {
                        url = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (url is null) return $"installer {assetName} not found in latest release";

            var target = Path.Combine(Path.GetTempPath(), assetName);
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(target, bytes);

            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return "INSTALL_LAUNCHED";
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "update download failed");
            return $"download failed: {e.Message}";
        }
    }

    private Version GetInstalledVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    private static Version? TryParseVersion(string tag)
    {
        var match = Regex.Match(tag, @"\d+(?:\.\d+){0,3}");
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static JsonElement? GetLatestPublishedRelease(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var release in root.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                continue;
            }

            return release;
        }

        return null;
    }
}

public sealed record UpdateCheckResult(
    string StatusText,
    string LastCheckText,
    string ReleaseUrl,
    bool IsUpdateAvailable);
