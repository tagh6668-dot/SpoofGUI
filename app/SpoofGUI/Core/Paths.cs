namespace SpoofGUI.Core;

internal static class Paths
{
    public static string AppDataDir
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(root, "SpoofGUI");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DatabasePath => Path.Combine(AppDataDir, "spoofgui.db");

    public static string XrayDir => Path.Combine(AppContext.BaseDirectory, "Xray");
    public static string XrayExePath => Path.Combine(XrayDir, "xray.exe");
    public static string XrayConfigPath => Path.Combine(AppDataDir, "xray-client.json");

    public static string SingBoxExePath
    {
        get
        {
            var engineDir = Path.Combine(AppContext.BaseDirectory, "engine");
            var primary = Path.Combine(engineDir, "sing-box.exe");
            if (File.Exists(primary)) return primary;

            var windowsNested = Path.Combine(engineDir, "Windows", "sing-box.exe");
            if (File.Exists(windowsNested)) return windowsNested;

            return primary;
        }
    }

    public static string SingBoxConfigPath =>
        Path.Combine(AppDataDir, "singbox-tun.json");
}
