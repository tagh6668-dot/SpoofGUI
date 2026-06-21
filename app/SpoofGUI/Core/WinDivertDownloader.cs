using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;

namespace SpoofGUI.Core;

internal static class WinDivertDownloader
{
    private const string Version = "2.2.2";
    private const string Package = "2.2.2-A";
    private static readonly Uri DownloadUri = new($"https://github.com/basil00/WinDivert/releases/download/v{Version}/WinDivert-{Package}.zip");

    public static async Task DownloadAsync(string arch, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (arch is not ("amd64" or "x86"))
            throw new ArgumentException("arch must be amd64 or x86", nameof(arch));

        var engineDir = Path.Combine(AppContext.BaseDirectory, "engine");
        Directory.CreateDirectory(engineDir);

        var tempRoot = Path.Combine(Paths.AppDataDir, "downloads", "windivert");
        var zipPath = Path.Combine(tempRoot, "windivert.zip");
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        Directory.CreateDirectory(tempRoot);

        try
        {
            progress?.Report("downloading WinDivert...");
            using var http = new HttpClient();
            using (var input = await http.GetStreamAsync(DownloadUri, ct).ConfigureAwait(false))
            using (var output = File.Create(zipPath))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }

            progress?.Report("extracting WinDivert...");
            ZipFile.ExtractToDirectory(zipPath, tempRoot);

            var subDir = arch == "x86" ? "x86" : "x64";
            var sourceDir = Path.Combine(tempRoot, $"WinDivert-{Package}", subDir);
            var dll = Path.Combine(sourceDir, "WinDivert.dll");
            var sys = Path.Combine(sourceDir, arch == "x86" ? "WinDivert32.sys" : "WinDivert64.sys");

            if (!File.Exists(dll) || !File.Exists(sys))
                throw new FileNotFoundException($"WinDivert files for {arch} not found in downloaded package.");

            File.Copy(dll, Path.Combine(engineDir, "WinDivert.dll"), overwrite: true);
            File.Copy(sys, Path.Combine(engineDir, Path.GetFileName(sys)), overwrite: true);
            progress?.Report("WinDivert ready");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
