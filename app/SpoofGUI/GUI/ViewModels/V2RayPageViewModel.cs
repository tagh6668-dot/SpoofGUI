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
    private readonly SubscriptionService _subscriptions;

    public V2RayPageViewModel(V2RayProfileRepository profiles, XrayCoreService xray, SingBoxTunnelService tunnel, ProxyPortSettings ports, AppSettings appSettings, ConnectionGuard guard, SubscriptionService subscriptions)
    {
        _profiles = profiles;
        _xray = xray;
        _tunnel = tunnel;
        _ports = ports;
        _appSettings = appSettings;
        _guard = guard;
        _subscriptions = subscriptions;
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

    public IReadOnlyList<Subscription> LoadSubscriptions() => _subscriptions.All();
    public Subscription AddSubscription(string name, string url) => _subscriptions.Add(name, url);
    public Task<SubUpdateResult> UpdateSubscriptionAsync(Subscription sub, CancellationToken ct = default) => _subscriptions.UpdateAsync(sub, ct);
    public Task<SubUpdateResult> UpdateAllSubscriptionsAsync(CancellationToken ct = default) => _subscriptions.UpdateAllAsync(ct);
    public void DeleteSubscription(Subscription sub, bool removeProfiles) => _subscriptions.Delete(sub, removeProfiles);
    public void SetSubscriptionAutoUpdate(Subscription sub, bool autoUpdate) => _subscriptions.SetAutoUpdate(sub, autoUpdate);

    public ImportResult ImportMany(string text, string mode)
    {
        var imported = new List<V2RayProfile>();
        var failed = 0;
        var duplicates = 0;
        foreach (var entry in V2RayConfigParser.SplitConfigs(text))
        {
            try
            {
                var profile = V2RayConfigParser.Parse(entry);
                profile.Mode = mode;
                if (_profiles.ExistsLike(profile))
                {
                    duplicates++;
                    continue;
                }
                _profiles.Upsert(profile);
                imported.Add(profile);
            }
            catch
            {
                failed++;
            }
        }

        return new ImportResult(imported, failed, duplicates);
    }

    public ImportPreview PreviewImport(string text)
    {
        var items = new List<ImportPreviewItem>();
        var invalid = 0;
        var duplicates = 0;
        foreach (var entry in V2RayConfigParser.SplitConfigs(text))
        {
            try
            {
                var profile = V2RayConfigParser.Parse(entry);
                var duplicate = _profiles.ExistsLike(profile);
                if (duplicate) duplicates++;
                items.Add(new ImportPreviewItem(profile.Name, profile.Protocol, profile.Address, profile.Port, profile.Security, duplicate));
            }
            catch { invalid++; }
        }
        return new ImportPreview(items, invalid, duplicates);
    }

    public void Save(V2RayProfile profile) => _profiles.Upsert(profile);
    public void Delete(V2RayProfile profile)
    {
        _profiles.Delete(profile.Id);
        _appSettings.ProxyChain = _appSettings.ProxyChain.Where(id => id != profile.Id).ToList();
    }
    public Task StartAsync(V2RayProfile profile) => _xray.StartAsync(profile);
    public Task StopAsync() => _xray.StopAsync();
    public Task<long> TestRealDelayAsync(V2RayProfile profile, CancellationToken ct = default) => _xray.TestRealDelayAsync(profile, ct);
    public void RememberPing(long id, string ping) => _profiles.RememberPing(id, ping);
    public void RecordPing(long id, long ms) => _profiles.RecordPing(id, ms);
    public string LatencySummary(long id) => _profiles.LatencySummary(id);
}

public sealed record ImportResult(IReadOnlyList<V2RayProfile> Imported, int Failed, int Duplicates);
public sealed record ImportPreview(IReadOnlyList<ImportPreviewItem> Items, int Invalid, int Duplicates);
public sealed record ImportPreviewItem(string Name, string Protocol, string Address, int Port, string Security, bool Duplicate);
