using System.Diagnostics;

namespace SpoofGUI.Core;

internal static class KillSwitch
{
    private const string RuleName = "SpoofGUI KillSwitch";

    public static void Block()
    {
        Unblock();
        Run("advfirewall", "firewall", "add", "rule", $"name={RuleName}", "dir=out", "action=block", "profile=any", "enable=yes");
    }

    public static void Unblock() => Run("advfirewall", "firewall", "delete", "rule", $"name={RuleName}");

    private static void Run(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch (Exception e)
        {
            AppLog.Warn($"killswitch netsh: {e.Message}");
        }
    }
}
