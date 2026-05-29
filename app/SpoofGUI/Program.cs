using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SpoofGUI.Core;

namespace SpoofGUI;

public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        if (!IsRunningAsAdministrator())
        {
            RelaunchAsAdministrator(args);
            return;
        }

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void RelaunchAsAdministrator(string[] args)
    {

        var exe = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exe))
            exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = args.Length > 0 ? string.Join(' ', args.Select(QuoteArg)) : string.Empty,
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception e)
        {

            CrashLog.Write("Elevation", e);
        }
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;
}
