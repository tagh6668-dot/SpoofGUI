using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using SpoofGUI.Core;

namespace SpoofGUI;

public static class WpfProgram
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint dwProcessId);
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--health-check", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(AttachParentProcess);
            var services = Bootstrap.BuildServiceProvider();
            Console.WriteLine(services.GetRequiredService<DiagnosticsService>().BuildHealthText());
            return;
        }

        if (!IsRunningAsAdministrator())
        {
            RelaunchAsAdministrator(args);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            CrashLog.Write("WpfUnhandledException", e.ExceptionObject as Exception);
            AppLog.Error($"FATAL (Wpf): {(e.ExceptionObject as Exception)?.Message}");
        };

        var app = new WpfApp();
        app.InitializeComponent();
        app.Run();
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
            CrashLog.Write("WpfElevation", e);
        }
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;
}
