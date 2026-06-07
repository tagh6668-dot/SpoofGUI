using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Engine;
using SpoofGUI.Models;

namespace SpoofGUI.GUI.ViewModels;

public sealed class RoutingPageViewModel
{
    private readonly RoutingRuleRepository _rules;
    private readonly V2RayProfileRepository _profiles;
    private readonly AppSettings _appSettings;
    private readonly SingBoxTunnelService _tunnel;
    private readonly ConnectionGuard _guard;

    public RoutingPageViewModel(RoutingRuleRepository rules, V2RayProfileRepository profiles, AppSettings appSettings, SingBoxTunnelService tunnel, ConnectionGuard guard)
    {
        _rules = rules;
        _profiles = profiles;
        _appSettings = appSettings;
        _tunnel = tunnel;
        _guard = guard;
    }

    public bool TunnelMode => _appSettings.V2RayMode == "Tunnel";
    public bool TunnelRunning => _tunnel.IsRunning;

    public async Task<string> ApplyAsync()
    {
        if (!_tunnel.IsRunning) return "saved";
        try
        {
            _guard.DisarmV2Ray();
            var reloaded = await Task.Run(() => _tunnel.Reload());
            _guard.ArmV2Ray();
            return reloaded ? "applied · tunnel reloaded" : "saved";
        }
        catch (Exception e)
        {
            AppLog.Warn($"routing apply (tunnel reload) failed: {e.Message}");
            return $"saved, but reload failed: {e.Message}";
        }
    }

    public IReadOnlyList<RoutingRule> LoadRules() => _rules.All();

    public void SaveRule(RoutingRule rule) => _rules.Upsert(rule);

    public void DeleteRule(RoutingRule rule) => _rules.Delete(rule.Id);

    public IReadOnlyList<V2RayProfile> AvailableProfiles(IEnumerable<long>? excludedProfileIds = null)
    {
        var excluded = excludedProfileIds is null ? [] : excludedProfileIds.ToHashSet();
        return _profiles.All().Where(p => p.Id != 0 && !excluded.Contains(p.Id)).ToList();
    }

    public IReadOnlyList<ChainHop> LoadChain()
    {
        var byId = _profiles.All().ToDictionary(p => p.Id);
        var hops = new List<ChainHop>();
        foreach (var id in _appSettings.ProxyChain)
        {
            if (!byId.TryGetValue(id, out var p)) continue;
            hops.Add(new ChainHop { ProfileId = p.Id, Name = p.Name, Protocol = p.Protocol, Address = p.Address });
        }
        return hops;
    }

    public void SaveChain(IEnumerable<long> profileIds) =>
        _appSettings.ProxyChain = profileIds.Where(id => id > 0).Distinct().ToList();

    public void RemoveFromChain(IEnumerable<long> profileIds)
    {
        var remove = profileIds.ToHashSet();
        if (remove.Count == 0) return;
        SaveChain(_appSettings.ProxyChain.Where(id => !remove.Contains(id)));
    }

    public void ClearChain() => _appSettings.ProxyChain = [];
}
