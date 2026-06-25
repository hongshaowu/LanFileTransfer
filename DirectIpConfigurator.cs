using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;

namespace LanFileTransfer;

internal static class DirectIpConfigurator
{
    private const string DirectPrefix = "192.168.10.";
    private const string DirectMask = "255.255.255.0";
    private const int TransferPort = LanTransferService.DefaultPort;
    private const int DiscoveryPort = 38384;

    public static void EnsureDirectStaticIp(Action<string>? log)
    {
        try
        {
            NetworkInterface? adapter = FindDirectEthernetAdapter(log);
            if (adapter == null)
            {
                Log(log, "未找到适合自动分配直连 IP 的有线网卡");
                return;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            foreach (UnicastIPAddressInformation addressInfo in properties.UnicastAddresses)
            {
                if (addressInfo.Address.AddressFamily == AddressFamily.InterNetwork
                    && addressInfo.Address.ToString().StartsWith(DirectPrefix, StringComparison.Ordinal))
                {
                    Log(log, "直连网卡已有 192 静态网段 IP: " + adapter.Name + " -> " + addressInfo.Address);
                    return;
                }
            }

            string ip = BuildStableDirectIp(adapter);
            Log(log, "直连网卡未检测到 192.168.10.x，准备自动分配: " + adapter.Name + " -> " + ip);
            SetStaticIp(adapter.Name, ip, log);
        }
        catch (Exception ex)
        {
            Log(log, "自动分配直连 IP 失败: " + ex.Message);
        }
    }

    public static void EnsureFirewallRules(Action<string>? log)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            string appRule = "LanFileTransfer App";
            string tcpRule = "LanFileTransfer TCP " + TransferPort;
            string udpRule = "LanFileTransfer UDP " + DiscoveryPort;
            string clearProgramRulesCommand = BuildClearProgramFirewallRulesCommand(exePath);
            string deleteAppArguments = "advfirewall firewall delete rule name=\"" + appRule + "\"";
            string deleteTcpArguments = "advfirewall firewall delete rule name=\"" + tcpRule + "\"";
            string deleteUdpArguments = "advfirewall firewall delete rule name=\"" + udpRule + "\"";
            string appArguments = "advfirewall firewall add rule name=\"" + appRule + "\" dir=in action=allow program=\"" + exePath + "\" enable=yes profile=any";
            string tcpArguments = "advfirewall firewall add rule name=\"" + tcpRule + "\" dir=in action=allow protocol=TCP localport=" + TransferPort + " profile=any";
            string udpArguments = "advfirewall firewall add rule name=\"" + udpRule + "\" dir=in action=allow protocol=UDP localport=" + DiscoveryPort + " profile=any";

            if (IsAdministrator())
            {
                if (!string.IsNullOrWhiteSpace(clearProgramRulesCommand))
                {
                    RunShellCommand(clearProgramRulesCommand, false, log, false);
                }

                RunNetsh(deleteAppArguments, false, log, false);
                RunNetsh(deleteTcpArguments, false, log, false);
                RunNetsh(deleteUdpArguments, false, log, false);
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    RunNetsh(appArguments, false, log, true);
                }

                RunNetsh(tcpArguments, false, log, true);
                RunNetsh(udpArguments, false, log, true);
                return;
            }

            List<string> commands = new List<string>();
            if (!string.IsNullOrWhiteSpace(clearProgramRulesCommand))
            {
                commands.Add(clearProgramRulesCommand);
            }

            commands.Add("netsh " + deleteAppArguments);
            commands.Add("netsh " + deleteTcpArguments);
            commands.Add("netsh " + deleteUdpArguments);
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                commands.Add("netsh " + appArguments);
            }

            commands.Add("netsh " + tcpArguments);
            commands.Add("netsh " + udpArguments);

            Log(log, "放行传输端口需要管理员权限，即将弹出 Windows 授权窗口");
            RunElevatedCommand(string.Join(" & ", commands), log);
        }
        catch (Exception ex)
        {
            Log(log, "配置 Windows 防火墙失败: " + ex.Message);
        }
    }

    private static NetworkInterface? FindDirectEthernetAdapter(Action<string>? log)
    {
        NetworkInterface? best = null;
        int bestScore = int.MinValue;
        NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
        for (int i = 0; i < adapters.Length; i++)
        {
            NetworkInterface adapter = adapters[i];
            if (adapter.OperationalStatus != OperationalStatus.Up || !IsEthernet(adapter.NetworkInterfaceType))
            {
                continue;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();
            if (HasIPv4Gateway(properties))
            {
                continue;
            }

            int score = 100;
            if (HasIPv4Prefix(properties, DirectPrefix))
            {
                score += 1000;
            }

            if (HasIPv4Prefix(properties, "169.254."))
            {
                score += 200;
            }

            if (adapter.Speed > 0)
            {
                score += Math.Min(100, (int)(adapter.Speed / 10000000));
            }

            Log(log, "直连候选网卡: " + adapter.Name + "，评分: " + score);
            if (score > bestScore)
            {
                bestScore = score;
                best = adapter;
            }
        }

        return best;
    }

    private static bool HasIPv4Gateway(IPInterfaceProperties properties)
    {
        foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
        {
            if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasIPv4Prefix(IPInterfaceProperties properties, string prefix)
    {
        foreach (UnicastIPAddressInformation addressInfo in properties.UnicastAddresses)
        {
            if (addressInfo.Address.AddressFamily == AddressFamily.InterNetwork
                && addressInfo.Address.ToString().StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEthernet(NetworkInterfaceType type)
    {
        return type == NetworkInterfaceType.Ethernet
            || type == NetworkInterfaceType.GigabitEthernet
            || type == NetworkInterfaceType.FastEthernetFx
            || type == NetworkInterfaceType.FastEthernetT;
    }

    private static string BuildStableDirectIp(NetworkInterface adapter)
    {
        byte[] bytes = adapter.GetPhysicalAddress().GetAddressBytes();
        int hash = 17;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash = hash * 31 + bytes[i];
        }

        int last = 20 + Math.Abs(hash % 200);
        return DirectPrefix + last;
    }

    private static void SetStaticIp(string adapterName, string ip, Action<string>? log)
    {
        string arguments = "interface ipv4 set address name=\"" + adapterName.Replace("\"", "\\\"") + "\" static " + ip + " " + DirectMask;
        if (IsAdministrator())
        {
            RunNetsh(arguments, false, log, true);
            return;
        }

        Log(log, "自动分配直连 IP 需要管理员权限，即将弹出 Windows 授权窗口");
        RunNetsh(arguments, true, log);
    }

    private static void RunNetsh(string arguments, bool elevate, Action<string>? log)
    {
        RunNetsh(arguments, elevate, log, true);
    }

    private static void RunNetsh(string arguments, bool elevate, Action<string>? log, bool logSuccess)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            Verb = elevate ? "runas" : string.Empty,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate
        };

        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            Log(log, "无法启动 netsh");
            return;
        }

        process.WaitForExit();
        if (elevate)
        {
            Log(log, "直连 IP 配置命令已提交，请稍等几秒后查看本机 IPv4");
            return;
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (process.ExitCode == 0)
        {
            if (logSuccess)
            {
                Log(log, "netsh 命令执行完成: " + arguments);
            }
        }
        else
        {
            Log(log, "netsh 配置失败: " + (string.IsNullOrWhiteSpace(error) ? output : error));
        }
    }

    private static void RunElevatedCommand(string command, Action<string>? log)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = true,
            CreateNoWindow = true,
            Verb = "runas"
        };

        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            Log(log, "无法启动管理员命令");
            return;
        }

        process.WaitForExit();
        Log(log, "Windows 防火墙规则配置命令已提交");
    }

    private static string BuildClearProgramFirewallRulesCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return string.Empty;
        }

        string escapedPath = exePath.Replace("'", "''");
        return "powershell -NoProfile -ExecutionPolicy Bypass -Command \"Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' } | Get-NetFirewallApplicationFilter | Where-Object { $_.Program -eq '" + escapedPath + "' } | ForEach-Object { Remove-NetFirewallRule -AssociatedNetFirewallApplicationFilter $_ }\"";
    }

    private static void RunShellCommand(string command, bool elevate, Action<string>? log, bool logSuccess)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + command,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            Verb = elevate ? "runas" : string.Empty,
            RedirectStandardOutput = !elevate,
            RedirectStandardError = !elevate
        };

        using Process? process = Process.Start(startInfo);
        if (process == null)
        {
            Log(log, "无法启动命令: " + command);
            return;
        }

        process.WaitForExit();
        if (elevate)
        {
            return;
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (process.ExitCode == 0)
        {
            if (logSuccess)
            {
                Log(log, "命令执行完成: " + command);
            }
        }
        else
        {
            Log(log, "命令执行失败: " + (string.IsNullOrWhiteSpace(error) ? output : error));
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void Log(Action<string>? log, string message)
    {
        log?.Invoke(message);
    }
}
