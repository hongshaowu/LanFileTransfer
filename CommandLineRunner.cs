namespace LanFileTransfer;

internal static class CommandLineRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string mode = args[0].ToLowerInvariant();
            LanTransferService service = new LanTransferService();

            if (mode == "-h" || mode == "--help" || mode == "help" || mode == "/?")
            {
                PrintUsage();
                return 0;
            }

            if (mode == "recv" || mode == "receive")
            {
                string outputDir = ".";
                int port = LanTransferService.DefaultPort;
                bool overwrite = false;

                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--out" && i + 1 < args.Length)
                    {
                        outputDir = args[++i];
                    }
                    else if (arg == "--port" && i + 1 < args.Length)
                    {
                        port = LanTransferService.ParsePort(args[++i]);
                    }
                    else if (arg == "--overwrite")
                    {
                        overwrite = true;
                    }
                    else
                    {
                        throw new ArgumentException("recv 参数无法识别: " + arg);
                    }
                }

                await service.ReceiveAsync(outputDir, port, overwrite, Console.WriteLine, null, CancellationToken.None);
                return 0;
            }

            if (mode == "send-all")
            {
                if (args.Length < 3)
                {
                    throw new ArgumentException("send-all 需要目标 IP 和源目录路径");
                }

                string host = args[1];
                string sourceDir = args[2];
                int port = ParsePortFromArgs(args, 3);

                await service.SendAllAsync(host, port, sourceDir, LanTransferService.DefaultParallelConnections, Console.WriteLine, null, CancellationToken.None);
                return 0;
            }

            if (mode == "send-selected" || mode == "send")
            {
                if (args.Length < 3)
                {
                    throw new ArgumentException(mode + " 需要目标 IP 和至少一个文件/文件夹路径");
                }

                string host = args[1];
                int port = LanTransferService.DefaultPort;
                List<string> sourcePaths = new List<string>();

                for (int i = 2; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (arg == "--port" && i + 1 < args.Length)
                    {
                        port = LanTransferService.ParsePort(args[++i]);
                    }
                    else
                    {
                        sourcePaths.Add(arg);
                    }
                }

                await service.SendSelectedAsync(host, port, sourcePaths, LanTransferService.DefaultParallelConnections, Console.WriteLine, null, CancellationToken.None);
                return 0;
            }

            Console.Error.WriteLine("未知命令: " + args[0]);
            PrintUsage();
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败: " + ex.Message);
            return 2;
        }
    }

    private static int ParsePortFromArgs(string[] args, int startIndex)
    {
        int port = LanTransferService.DefaultPort;
        for (int i = startIndex; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--port" && i + 1 < args.Length)
            {
                port = LanTransferService.ParsePort(args[++i]);
            }
            else
            {
                throw new ArgumentException("参数无法识别: " + arg);
            }
        }

        return port;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("LanFileTransfer - 网线直连/局域网文件传输工具");
        Console.WriteLine();
        Console.WriteLine("接收端:");
        Console.WriteLine("  LanFileTransfer recv --out D:\\Receive [--port 38383] [--overwrite]");
        Console.WriteLine();
        Console.WriteLine("发送端:");
        Console.WriteLine("  LanFileTransfer send-all 192.168.1.20 D:\\Data [--port 38383]");
        Console.WriteLine("  LanFileTransfer send-selected 192.168.1.20 D:\\Data\\file.zip D:\\Data\\Folder [--port 38383]");
    }
}
