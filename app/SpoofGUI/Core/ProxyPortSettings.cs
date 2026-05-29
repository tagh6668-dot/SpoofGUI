using SpoofGUI.Database;

namespace SpoofGUI.Core;

public sealed class ProxyPortSettings
{
    public const int DefaultSocksPort = 20882;
    public const int DefaultHttpPort = 20883;

    private const string SocksKey = "socks_port";
    private const string HttpKey = "http_port";

    private readonly SettingsRepository _settings;

    public ProxyPortSettings(SettingsRepository settings) => _settings = settings;

    public int SocksPort => Read(SocksKey, DefaultSocksPort);
    public int HttpPort => Read(HttpKey, DefaultHttpPort);

    public void Set(int socksPort, int httpPort)
    {
        ValidatePort(socksPort, nameof(socksPort));
        ValidatePort(httpPort, nameof(httpPort));
        if (socksPort == httpPort)
            throw new ArgumentException("SOCKS and HTTP ports must differ");
        _settings.Set(SocksKey, socksPort.ToString());
        _settings.Set(HttpKey, httpPort.ToString());
    }

    private int Read(string key, int fallback)
    {
        var raw = _settings.Get(key);
        return int.TryParse(raw, out var v) && v is > 0 and <= 65535 ? v : fallback;
    }

    private static void ValidatePort(int port, string name)
    {
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(name, port, "port must be 1-65535");
    }
}
