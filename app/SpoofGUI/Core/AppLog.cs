using System.Collections.Concurrent;
using System.IO;

namespace SpoofGUI.Core;

public static class AppLog
{
    private const int MaxEntries = 500;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int RotatedFiles = 3;
    private static readonly ConcurrentQueue<string> Entries = new();
    private static readonly object FileLock = new();
    private static int _sinceSizeCheck;

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpoofGUI");

    public static string LogFilePath => Path.Combine(LogDirectory, "app.log");

    public static void Info(string message) => Add("info", message);
    public static void Warn(string message) => Add("warn", message);
    public static void Error(string message) => Add("error", message);

    public static IReadOnlyList<string> Snapshot() => Entries.ToArray();

    public static void Clear()
    {
        while (Entries.TryDequeue(out _)) { }
    }

    private static void Add(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        Entries.Enqueue(line);
        while (Entries.Count > MaxEntries && Entries.TryDequeue(out _)) { }
        WriteToFile(line);
    }

    private static void WriteToFile(string line)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        if (++_sinceSizeCheck < 50 && File.Exists(LogFilePath)) return;
        _sinceSizeCheck = 0;

        var info = new FileInfo(LogFilePath);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        var oldest = $"{LogFilePath}.{RotatedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = RotatedFiles - 1; i >= 1; i--)
        {
            var src = $"{LogFilePath}.{i}";
            if (File.Exists(src)) File.Move(src, $"{LogFilePath}.{i + 1}");
        }
        File.Move(LogFilePath, $"{LogFilePath}.1");
    }
}
