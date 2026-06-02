using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using SpoofGUI.Core;
using SpoofGUI.Models;

namespace SpoofGUI.Engine;

public sealed class EngineSupervisor : IDisposable
{
    private readonly ILogger<EngineSupervisor> _log;
    private readonly AppSettings _appSettings;
    private readonly SniSpoofEngine _engine = new();
    private DateTimeOffset? _startedAt;
    private string? _lastError;

    public bool IsRunning => _engine.IsRunning;
    public TimeSpan Uptime => _startedAt is null ? TimeSpan.Zero : DateTimeOffset.Now - _startedAt.Value;

    public EngineSupervisor(ILogger<EngineSupervisor> log, AppSettings appSettings)
    {
        _log = log;
        _appSettings = appSettings;
    }

    public void Start(SpoofProfile profile)
    {
        if (IsRunning) return;

        _lastError = null;
        ProxyPortKiller.KillOwners(profile.ListenPort);

        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        EnsureFirewallRule(profile.ListenPort, exe);

        try
        {
            _engine.Start(profile, _appSettings.FastMode);
        }
        catch (Exception e)
        {
            _lastError = e.Message;
            _log.LogError(e, "SNI engine start failed");
            AppLog.Error($"SNI engine start failed: {e.Message}");
            RemoveFirewallRule();
            throw;
        }

        _startedAt = DateTimeOffset.Now;
    }

    public bool WaitForListener(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning) return false;
            if (IsListening(host, port)) return true;
            Thread.Sleep(100);
        }

        return false;
    }

    public string GetStartupFailureMessage()
    {
        if (_lastError is not null)
            return $"engine failed to start: {_lastError}";

        return "engine did not stay running. Run SpoofGUI as administrator and check Logs.";
    }

    public void Stop()
    {
        try { _engine.Stop(); }
        catch (Exception e) { _log.LogWarning(e, "stop"); }
        RemoveFirewallRule();
        _startedAt = null;
    }

    private const string FirewallRuleName = "SpoofGUI SNI-Spoof Listener";

    private static void EnsureFirewallRule(int listenPort, string exePath)
    {
        RemoveFirewallRule();
        var ok = RunNetsh(
            "advfirewall", "firewall", "add", "rule",
            $"name={FirewallRuleName}",
            "dir=in", "action=allow", "protocol=TCP",
            $"localport={listenPort}",
            "remoteip=LocalSubnet",
            $"program={exePath}",
            "profile=any", "enable=yes");
        if (ok)
            AppLog.Info($"firewall: inbound allowed on TCP {listenPort} (LAN) for the SNI listener");
        else
            AppLog.Warn($"firewall: could not add inbound rule for TCP {listenPort}; LAN devices may be blocked");
    }

    private static void RemoveFirewallRule()
    {
        RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={FirewallRuleName}");
    }

    private static bool RunNetsh(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch (Exception e)
        {
            AppLog.Warn($"netsh failed: {e.Message}");
            return false;
        }
    }

    private static bool IsListening(string host, int port)
    {
        var expected = IPAddress.TryParse(host, out var parsed) ? parsed : IPAddress.Any;
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
        {
            if (ep.Port != port) continue;
            if (expected.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(expected))
                return true;
        }

        return false;
    }

    public void Dispose() => Stop();
}
