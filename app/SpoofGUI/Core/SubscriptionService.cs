using System.Net.Http;
using Microsoft.Extensions.Logging;
using SpoofGUI.Database;
using SpoofGUI.Models;

namespace SpoofGUI.Core;

public sealed class SubscriptionService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly SubscriptionRepository _subs;
    private readonly V2RayProfileRepository _profiles;
    private readonly AppSettings _appSettings;
    private readonly ILogger<SubscriptionService> _log;
    private Timer? _timer;

    public SubscriptionService(SubscriptionRepository subs, V2RayProfileRepository profiles, AppSettings appSettings, ILogger<SubscriptionService> log)
    {
        _subs = subs;
        _profiles = profiles;
        _appSettings = appSettings;
        _log = log;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SpoofGUI/1.0");
        return client;
    }

    public IReadOnlyList<Subscription> All() => _subs.All();

    public Subscription Add(string name, string url)
    {
        var sub = new Subscription
        {
            Name = string.IsNullOrWhiteSpace(name) ? "subscription" : name.Trim(),
            Url = (url ?? "").Trim(),
        };
        _subs.Upsert(sub);
        return sub;
    }

    public async Task<SubUpdateResult> UpdateAsync(Subscription sub, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sub.Url))
            return new SubUpdateResult(sub, 0, "no url");

        try
        {
            var body = await Http.GetStringAsync(sub.Url, ct);
            var parsed = V2RayConfigParser.ParseSubscriptionBody(body);
            if (parsed.Count == 0)
                return new SubUpdateResult(sub, 0, "no configs found");

            _profiles.DeleteBySubscription(sub.Id);
            var mode = _appSettings.V2RayMode;
            foreach (var p in parsed)
            {
                p.Id = 0;
                p.SubscriptionId = sub.Id;
                p.Mode = mode;
                _profiles.Upsert(p);
            }

            sub.LastCount = parsed.Count;
            sub.LastUpdated = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _subs.Upsert(sub);
            AppLog.Info($"subscription '{sub.Name}': imported {parsed.Count} configs");
            return new SubUpdateResult(sub, parsed.Count, "ok");
        }
        catch (Exception e)
        {
            AppLog.Warn($"subscription '{sub.Name}' update failed: {e.Message}");
            return new SubUpdateResult(sub, 0, e.Message);
        }
    }

    public async Task<SubUpdateResult> UpdateAllAsync(CancellationToken ct = default)
    {
        var subs = _subs.All();
        var total = 0;
        var ok = 0;
        foreach (var s in subs)
        {
            var r = await UpdateAsync(s, ct);
            if (r.Status == "ok") { ok++; total += r.Count; }
        }
        return new SubUpdateResult(null, total, $"{ok}/{subs.Count} updated · {total} configs");
    }

    public void Delete(Subscription sub, bool removeProfiles)
    {
        if (removeProfiles) _profiles.DeleteBySubscription(sub.Id);
        _subs.Delete(sub.Id);
    }

    public void SetAutoUpdate(Subscription sub, bool autoUpdate)
    {
        sub.AutoUpdate = autoUpdate;
        _subs.Upsert(sub);
    }

    public void StartAutoTimer()
    {
        _timer ??= new Timer(
            _ => { try { _ = UpdateAutoAsync(); } catch { } },
            null,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromHours(6));
    }

    private async Task UpdateAutoAsync()
    {
        foreach (var s in _subs.All().Where(x => x.AutoUpdate))
            await UpdateAsync(s, CancellationToken.None);
    }
}

public sealed record SubUpdateResult(Subscription? Sub, int Count, string Status);
