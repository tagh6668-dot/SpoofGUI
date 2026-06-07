using System.Runtime.InteropServices;

namespace SpoofGUI.Engine;

[StructLayout(LayoutKind.Sequential)]
internal struct WinDivertAddress
{
    public long Timestamp;
    public uint Flags;
    public uint Reserved2;
    private ulong _union0;
    private ulong _union1;
    private ulong _union2;
    private ulong _union3;
    private ulong _union4;
    private ulong _union5;
    private ulong _union6;
    private ulong _union7;

    public readonly bool Outbound => ((Flags >> 17) & 1) != 0;
}

internal static class WinDivert
{
    private const string Library = "WinDivert.dll";

    public const int LayerNetwork = 0;

    static WinDivert()
    {
        NativeLibrary.SetDllImportResolver(typeof(WinDivert).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, Library, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var path in CandidatePaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var engineDir = Path.Combine(AppContext.BaseDirectory, "engine");
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(engineDir, Library);
        yield return Path.Combine(baseDir, Library);
        if (Environment.Is64BitProcess)
        {
            yield return Path.Combine(engineDir, "WinDivert64.dll");
            yield return Path.Combine(baseDir, "WinDivert64.dll");
        }
    }

    public static string EngineDirectory => Path.Combine(AppContext.BaseDirectory, "engine");

    public static string RequiredDriverName => Environment.Is64BitProcess ? "WinDivert64.sys" : "WinDivert32.sys";

    public static bool IsAvailable() =>
        CandidatePaths().Any(File.Exists) && File.Exists(Path.Combine(EngineDirectory, RequiredDriverName));

    public static void EnsureLoadable()
    {
        if (IsAvailable()) return;
        throw new FileNotFoundException($"WinDivert files not found under {EngineDirectory}");
    }

    [DllImport(Library, CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr WinDivertOpen(string filter, int layer, short priority, ulong flags);

    [DllImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, ref WinDivertAddress addr);

    [DllImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint packetLen, out uint sendLen, ref WinDivertAddress addr);

    [DllImport(Library, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinDivertClose(IntPtr handle);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinDivertHelperCalcChecksums(byte[] packet, uint packetLen, ref WinDivertAddress addr, ulong flags);

    public static IntPtr Open(string filter)
    {
        var handle = WinDivertOpen(filter, LayerNetwork, 0, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "WinDivertOpen failed");
        return handle;
    }

    public static bool Recv(IntPtr handle, byte[] buffer, out uint received, ref WinDivertAddress addr) =>
        WinDivertRecv(handle, buffer, (uint)buffer.Length, out received, ref addr);

    public static void Send(IntPtr handle, byte[] packet, uint length, ref WinDivertAddress addr) =>
        WinDivertSend(handle, packet, length, out _, ref addr);

    public static void CalcChecksums(byte[] packet, uint length, ref WinDivertAddress addr) =>
        WinDivertHelperCalcChecksums(packet, length, ref addr, 0);

    public static void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) WinDivertClose(handle);
    }
}
