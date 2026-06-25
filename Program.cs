using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Principal;
using Avalonia;

namespace LanFileTransfer;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!IsAdministrator())
            {
                RelaunchAsAdministrator(args);
                return 0;
            }

            if (args.Length > 0)
            {
                AttachConsoleOutput();
                return await CommandLineRunner.RunAsync(args);
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            WriteStartupError(ex);
            return 2;
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchAsAdministrator(string[] args)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("无法获取程序路径，不能以管理员身份重新启动");
        }

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = BuildArguments(args)
        };
        Process.Start(startInfo);
    }

    private static string BuildArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        string[] quoted = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            quoted[i] = "\"" + args[i].Replace("\"", "\\\"") + "\"";
        }

        return string.Join(" ", quoted);
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    private static void AttachConsoleOutput()
    {
        AttachConsole(AttachParentProcess);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    private static void WriteStartupError(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, "LanFileTransfer.startup.log");
            File.WriteAllText(logPath, ex.ToString());
        }
        catch
        {
        }
    }
}
