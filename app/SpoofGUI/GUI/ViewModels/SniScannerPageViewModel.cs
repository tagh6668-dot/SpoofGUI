using SpoofGUI.Core;
using SpoofGUI.Database;
using SpoofGUI.Models;

namespace SpoofGUI.GUI.ViewModels;

public sealed class SniScannerPageViewModel
{
    private readonly SniScannerService _scanner;
    private readonly ProfileRepository _spoofProfiles;

    public SniScannerPageViewModel(SniScannerService scanner, ProfileRepository spoofProfiles)
    {
        _scanner = scanner;
        _spoofProfiles = spoofProfiles;
    }

    public IReadOnlyList<string> ParseDomains(string text) => SniScannerService.ParseDomains(text);

    public IReadOnlyList<string> ProfileSnis() =>
        _spoofProfiles.All()
            .Select(p => p.FakeSni)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public int ProfileCount() => _spoofProfiles.All().Count;

    public IReadOnlyList<string> BuiltinCloudflareList() => SniScannerService.BuiltinCloudflareList();

    public Task<IReadOnlyList<SniScanResult>> ScanAsync(
        IReadOnlyList<string> domains,
        bool verifyHttp,
        IProgress<int>? progress,
        CancellationToken ct)
        => _scanner.ScanAsync(domains, verifyHttp, SniScannerService.DefaultConcurrency, SniScannerService.DefaultTimeout, progress, ct);

    public string ApplyBestToActive(IReadOnlyList<SniScanResult> results)
    {
        var best = results.FirstOrDefault(r => r.UsableAsSni)
                   ?? results.FirstOrDefault(r => r.IsCloudflare && r.TlsOk);
        if (best is null) return "no usable Cloudflare SNI in results — scan first";

        var active = _spoofProfiles.GetActive();
        if (active is null)
        {
            var existing = _spoofProfiles.All();
            if (existing.Count >= ConfigPageViewModel.MaxProfiles)
                return $"profile limit reached (max {ConfigPageViewModel.MaxProfiles}) — delete one in Configs first";

            _spoofProfiles.Upsert(new SpoofProfile
            {
                Name = best.Domain,
                ListenHost = "0.0.0.0",
                ListenPort = 40443,
                ConnectIp = string.IsNullOrWhiteSpace(best.Ip) ? "104.19.229.21" : best.Ip!,
                ConnectPort = 443,
                FakeSni = best.Domain,
                IsActive = true,
            });
            return $"created active profile \"{best.Domain}\" ({best.TlsMs} ms)";
        }

        active.FakeSni = best.Domain;
        if (!string.IsNullOrWhiteSpace(best.Ip)) active.ConnectIp = best.Ip!;
        _spoofProfiles.Upsert(active);
        return $"active profile \"{active.Name}\" → SNI {best.Domain} ({best.TlsMs} ms)";
    }

    public string CreateProfileFromResult(SniScanResult result)
    {
        var existing = _spoofProfiles.All();
        if (existing.Count >= ConfigPageViewModel.MaxProfiles)
            return $"profile limit reached (max {ConfigPageViewModel.MaxProfiles}) — delete one in Configs first";

        var name = UniqueName(existing, result.Domain);
        _spoofProfiles.Upsert(new SpoofProfile
        {
            Name = name,
            ListenHost = "0.0.0.0",
            ListenPort = 40443,
            ConnectIp = string.IsNullOrWhiteSpace(result.Ip) ? "104.19.229.21" : result.Ip,
            ConnectPort = 443,
            FakeSni = result.Domain,
            IsActive = existing.Count == 0,
        });

        return $"created profile \"{name}\" in Configs";
    }

    private static string UniqueName(IReadOnlyList<SpoofProfile> existing, string baseName)
    {
        var names = existing.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; i <= 99; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!names.Contains(candidate)) return candidate;
        }

        return $"{baseName} ({Guid.NewGuid():N})";
    }
}
