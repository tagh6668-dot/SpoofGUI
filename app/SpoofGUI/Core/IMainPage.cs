namespace SpoofGUI.Core;

public interface IMainPage
{
    void RenderIdle(string profileName, string flow, string sni);
    void RenderLive(string iface, ulong uptimeMs, uint conns);
    void RenderV2RayCard(bool live, string mode, int socksPort, int httpPort, string lastError);
    void RenderConnecting();
    void RenderError(string message);
}
