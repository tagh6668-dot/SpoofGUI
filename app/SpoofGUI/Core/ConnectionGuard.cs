using System.Collections.Concurrent;
using System.Timers;
using SpoofGUI.Engine;

namespace SpoofGUI.Core;

public sealed class ConnectionGuard : IDisposable
{
    private readonly AppSettings _app;
    private readonly EngineSupervisor _engine;
    private readonly XrayCoreService _xray;
    private readonly SingBoxTunnelService _tunnel;
    private readonly ConcurrentDictionary<string, Func<bool>> _armed = new();
    private readonly System.Timers.Timer _timer;
    private bool _blocked;

    public ConnectionGuard(AppSettings app, EngineSupervisor engine, XrayCoreService xray, SingBoxTunnelService tunnel)
    {
        _app = app;
        _engine = engine;
        _xray = xray;
        _tunnel = tunnel;
        _timer = new System.Timers.Timer(2000) { AutoReset = true };
        _timer.Elapsed += OnTick;
        _timer.Start();
    }

    public void ArmSni() => _armed["sni"] = () => _engine.IsRunning;
    public void ArmV2Ray() => _armed["v2ray"] = () => _xray.IsRunning || _tunnel.IsRunning;
    public void DisarmSni() => Disarm("sni");
    public void DisarmV2Ray() => Disarm("v2ray");

    private void Disarm(string key)
    {
        _armed.TryRemove(key, out _);
        if (_armed.IsEmpty) Unblock();
    }

    private void OnTick(object? sender, ElapsedEventArgs e)
    {
        if (!_app.KillSwitch)
        {
            Unblock();
            return;
        }

        if (_armed.IsEmpty) return;

        var dropped = _armed.Any(kv =>
        {
            try { return !kv.Value(); }
            catch { return false; }
        });

        if (dropped && !_blocked) Block();
    }

    private void Block()
    {
        try
        {
            KillSwitch.Block();
            _blocked = true;
            AppLog.Warn("kill switch: a connected core dropped — outbound traffic blocked. Disconnect to restore.");
        }
        catch (Exception ex) { AppLog.Warn($"kill switch block failed: {ex.Message}"); }
    }

    private void Unblock()
    {
        if (!_blocked) return;
        try { KillSwitch.Unblock(); }
        catch (Exception ex) { AppLog.Warn($"kill switch unblock failed: {ex.Message}"); }
        _blocked = false;
        AppLog.Info("kill switch: outbound traffic restored");
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        Unblock();
    }
}
