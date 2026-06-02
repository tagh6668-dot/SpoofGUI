using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace SpoofGUI.Core;

public sealed record SniScanResult(
    string Domain,
    string? Ip,
    bool IsCloudflare,
    bool TcpOk,
    bool TlsOk,
    int? HttpStatus,
    long TlsMs,
    string? Error)
{
    public bool UsableAsSni => IsCloudflare && TcpOk && TlsOk;

    public string IpText => Ip ?? "—";

    public string Detail
    {
        get
        {
            if (Error is not null) return $"{IpText} · {Error}";
            if (!TcpOk) return $"{IpText} · unreachable";
            if (!TlsOk) return $"{IpText} · TCP ok, TLS failed";
            var http = HttpStatus is int s ? $" · HTTP {s}" : "";
            return $"{IpText} · {TlsMs} ms · TLS ok{http}";
        }
    }

    public string CloudflareText => IsCloudflare ? "Cloudflare" : "not Cloudflare";
}

public sealed class SniScannerService
{
    public const int MaxDomains = 1000;
    public const int DefaultConcurrency = 50;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private static readonly string[] CloudflareCidrs =
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    ];

    private static readonly (uint Network, uint Mask)[] CloudflareRanges =
        CloudflareCidrs.Select(ParseCidr).ToArray();

    public static bool IsCloudflareIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var value = IpToUint(address);
        foreach (var (network, mask) in CloudflareRanges)
        {
            if ((value & mask) == network) return true;
        }

        return false;
    }

    private static IReadOnlyList<string>? _builtinList;

    public static IReadOnlyList<string> BuiltinCloudflareList()
    {
        if (_builtinList is not null) return _builtinList;

        var assembly = typeof(SniScannerService).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("scan-snis.txt", StringComparison.OrdinalIgnoreCase));

        if (name is null)
            return _builtinList = [];

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return _builtinList = ParseDomains(reader.ReadToEnd());
    }

    public static IReadOnlyList<string> ParseDomains(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var domains = new List<string>();
        foreach (var rawLine in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var scheme = line.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) line = line[(scheme + 3)..];

            var slash = line.IndexOf('/');
            if (slash >= 0) line = line[..slash];

            var colon = line.IndexOf(':');
            if (colon >= 0) line = line[..colon];

            line = line.Trim();
            if (line.Length == 0) continue;
            if (seen.Add(line)) domains.Add(line);
        }

        return domains;
    }

    public async Task<IReadOnlyList<SniScanResult>> ScanAsync(
        IReadOnlyList<string> domains,
        bool verifyHttp,
        int concurrency,
        TimeSpan timeout,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Clamp(concurrency, 1, 200));
        var completed = 0;

        var tasks = domains.Select(async domain =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await CheckOneAsync(domain, verifyHttp, timeout, ct);
            }
            finally
            {
                gate.Release();
                progress?.Report(Interlocked.Increment(ref completed));
            }
        });

        var results = await Task.WhenAll(tasks);
        return results
            .OrderByDescending(r => r.UsableAsSni)
            .ThenByDescending(r => r.IsCloudflare)
            .ThenBy(r => r.TlsOk ? r.TlsMs : long.MaxValue)
            .ToList();
    }

    private static async Task<SniScanResult> CheckOneAsync(string domain, bool verifyHttp, TimeSpan timeout, CancellationToken ct)
    {
        string? ip = null;
        bool isCloudflare = false, tcpOk = false, tlsOk = false;
        int? httpStatus = null;
        long tlsMs = 0;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var token = cts.Token;

            var addresses = await Dns.GetHostAddressesAsync(domain, token);
            var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null)
                return new SniScanResult(domain, null, false, false, false, null, 0, "no A record");

            ip = ipv4.ToString();
            isCloudflare = IsCloudflareIp(ip);

            using var client = new TcpClient();
            await client.ConnectAsync(ipv4, 443, token);
            tcpOk = true;

            var stopwatch = Stopwatch.StartNew();
            using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = domain,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, token);
            stopwatch.Stop();
            tlsOk = true;
            tlsMs = stopwatch.ElapsedMilliseconds;

            if (verifyHttp)
            {
                var request = Encoding.ASCII.GetBytes(
                    $"GET / HTTP/1.1\r\nHost: {domain}\r\nUser-Agent: SpoofGUI\r\nConnection: close\r\n\r\n");
                await ssl.WriteAsync(request, token);
                var buffer = new byte[256];
                var read = await ssl.ReadAsync(buffer, token);
                var head = Encoding.ASCII.GetString(buffer, 0, read);
                var match = Regex.Match(head, @"HTTP/\d\.\d (\d{3})");
                if (match.Success) httpStatus = int.Parse(match.Groups[1].Value);
            }

            return new SniScanResult(domain, ip, isCloudflare, tcpOk, tlsOk, httpStatus, tlsMs, null);
        }
        catch (OperationCanceledException)
        {
            return new SniScanResult(domain, ip, isCloudflare, tcpOk, tlsOk, httpStatus, tlsMs, "timeout");
        }
        catch (Exception e)
        {
            return new SniScanResult(domain, ip, isCloudflare, tcpOk, tlsOk, httpStatus, tlsMs, e.Message);
        }
    }

    private static (uint Network, uint Mask) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        var ip = IpToUint(IPAddress.Parse(parts[0]));
        var bits = int.Parse(parts[1]);
        var mask = bits == 0 ? 0u : 0xFFFFFFFFu << (32 - bits);
        return (ip & mask, mask);
    }

    private static uint IpToUint(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
