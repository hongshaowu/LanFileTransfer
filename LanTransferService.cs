using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LanFileTransfer;

internal sealed class LanTransferService
{
    public const int DefaultPort = 38383;

    private const int BufferSize = 1024 * 1024;
    private const int DiscoveryPort = 38384;
    public const int DefaultParallelConnections = 12;

    private const byte EntryEnd = 0;
    private const byte EntryDirectory = 1;
    private const byte EntryFile = 2;
    private const byte EntrySpeedTest = 8;
    private const byte EntrySessionEnd = 9;
    private const string DiscoveryRequest = "LFT_DISCOVER_REQUEST";
    private const string DiscoveryResponsePrefix = "LFT_DISCOVER_RESPONSE:";
    private const string Magic = "LFT3";
    private const string ProbeRequest = "PING";
    private const string ProbeResponse = "LFTPONG";

    public async Task ReceiveAsync(string outputDir, int port, bool overwrite, Action<string>? log, Action<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);

        using TcpListener listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        using CancellationTokenSource discoveryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task discoveryTask = RunDiscoveryResponderAsync(port, log, discoveryCts.Token);

        object pathLock = new object();
        List<Task> workers = new List<Task>();
        long receivedBytes = 0;
        long receivedFiles = 0;

        Log(log, "接收端已启动");
        Log(log, "监听端口: " + port);
        Log(log, "自动识别端口: " + DiscoveryPort);
        Log(log, "保存目录: " + outputDir);
        Log(log, "本机 IPv4: " + string.Join(", ", GetLocalIPv4Addresses()));
        Log(log, "等待发送端连接...");

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                NetworkStream stream = client.GetStream();
                string magic;
                try
                {
                    magic = Encoding.ASCII.GetString(await ReadExactAsync(stream, Magic.Length, cancellationToken));
                }
                catch (EndOfStreamException)
                {
                    client.Dispose();
                    continue;
                }

                if (magic == ProbeRequest)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(ProbeResponse), cancellationToken);
                    client.Dispose();
                    continue;
                }

                if (magic != Magic)
                {
                    client.Dispose();
                    Log(log, "忽略非传输连接: " + (client.Client.RemoteEndPoint?.ToString() ?? "unknown"));
                    continue;
                }

                byte firstEntryType = (await ReadExactAsync(stream, 1, cancellationToken))[0];
                if (firstEntryType == EntrySpeedTest)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        using (client)
                        {
                            long bytes = await ReceiveSpeedTestAsync(stream, progress, received =>
                            {
                                long total = Interlocked.Add(ref receivedBytes, received);
                                Report(progress, total, 0, "测速接收 " + FormatBytes(total));
                            }, cancellationToken);
                            Log(log, "测速连接完成: " + FormatBytes(bytes));
                        }
                    }, cancellationToken));
                    continue;
                }

                if (firstEntryType == EntrySessionEnd)
                {
                    client.Dispose();
                    break;
                }

                IPEndPoint? remote = client.Client.RemoteEndPoint as IPEndPoint;
                Log(log, "已连接 worker: " + (remote?.ToString() ?? "unknown"));
                workers.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        await ReceiveWorkerAsync(stream, outputDir, overwrite, firstEntryType, pathLock, log, progress, () =>
                        {
                            long files = Interlocked.Increment(ref receivedFiles);
                            return files;
                        }, bytes =>
                        {
                            long total = Interlocked.Add(ref receivedBytes, bytes);
                            Report(progress, total, 0, "已接收 " + FormatBytes(total));
                        }, cancellationToken);
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(workers);
            Log(log, "接收完成，共 " + receivedFiles + " 个文件，" + FormatBytes(receivedBytes));
            Report(progress, receivedBytes, receivedBytes, "接收完成: " + FormatBytes(receivedBytes));
        }
        finally
        {
            discoveryCts.Cancel();
            try
            {
                await discoveryTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task SendAllAsync(string host, int port, string sourceDir, int parallelConnections, Action<string>? log, Action<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        sourceDir = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException("找不到要发送的源目录: " + sourceDir);
        }

        await SendParallelAsync(host, port, parallelConnections, queue =>
        {
            foreach (string directory in EnumerateDirectories(sourceDir, log))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipSystemPath(directory))
                {
                    Log(log, "[跳过系统目录] " + directory);
                    continue;
                }

                queue.Add(SendItem.Directory(directory, Path.GetFileName(directory), true), cancellationToken);
                AddDirectoryTree(queue, directory, Path.GetFileName(directory), true, log, cancellationToken);
            }

            foreach (string file in EnumerateFiles(sourceDir, log))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ShouldSkipSystemPath(file))
                {
                    Log(log, "[跳过系统文件] " + file);
                    continue;
                }

                queue.Add(SendItem.File(file, Path.GetFileName(file), true), cancellationToken);
            }
        }, log, progress, cancellationToken);
    }

    public async Task SendSelectedAsync(string host, int port, IReadOnlyList<string> sourcePaths, int parallelConnections, Action<string>? log, Action<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("请先选择至少一个文件或文件夹");
        }

        await SendParallelAsync(host, port, parallelConnections, queue =>
        {
            Dictionary<string, int> usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sourcePaths.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourcePath = Path.GetFullPath(sourcePaths[i]);
                if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                {
                    Log(log, "[跳过] 找不到 " + sourcePath);
                    continue;
                }

                string topName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(topName))
                {
                    topName = GetDriveRootName(sourcePath);
                }

                topName = GetUniqueTopName(usedNames, topName);

                if (Directory.Exists(sourcePath))
                {
                    queue.Add(SendItem.Directory(sourcePath, topName, false), cancellationToken);
                    AddDirectoryTree(queue, sourcePath, topName, false, log, cancellationToken);
                    continue;
                }

                queue.Add(SendItem.File(sourcePath, topName, false), cancellationToken);
            }
        }, log, progress, cancellationToken);
    }

    public async Task RunSpeedTestAsync(string host, int port, int parallelConnections, long bytesPerConnection, Action<string>? log, Action<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        parallelConnections = Math.Clamp(parallelConnections, 1, 32);
        long totalBytes = bytesPerConnection * parallelConnections;
        long sentBytes = 0;
        byte[] buffer = new byte[BufferSize];
        new Random(12345).NextBytes(buffer);

        Log(log, "开始网络测速，并行连接数: " + parallelConnections + "，总数据: " + FormatBytes(totalBytes));
        Task[] workers = new Task[parallelConnections];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                using TcpClient client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                using NetworkStream stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(Magic), cancellationToken);
                await stream.WriteAsync(new[] { EntrySpeedTest }, cancellationToken);
                await WriteInt64Async(stream, bytesPerConnection, cancellationToken);

                long remaining = bytesPerConnection;
                while (remaining > 0)
                {
                    int write = (int)Math.Min(buffer.Length, remaining);
                    await stream.WriteAsync(buffer.AsMemory(0, write), cancellationToken);
                    remaining -= write;
                    long total = Interlocked.Add(ref sentBytes, write);
                    Report(progress, total, totalBytes, "网络测速");
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
        await SendSessionEndAsync(host, port, cancellationToken);
        Log(log, "网络测速完成，共发送 " + FormatBytes(sentBytes));
        Report(progress, sentBytes, totalBytes, "网络测速完成");
    }

    public async Task<string?> DiscoverReceiverAsync(int transferPort, Action<string>? log, CancellationToken cancellationToken)
    {
        using UdpClient udpClient = new UdpClient(0);
        udpClient.EnableBroadcast = true;

        byte[] payload = Encoding.UTF8.GetBytes(DiscoveryRequest + ":" + transferPort);
        List<IPEndPoint> targets = GetDiscoveryTargets(log);

        Log(log, "正在自动识别接收端，广播目标数: " + targets.Count);
        for (int i = 0; i < 3; i++)
        {
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                await udpClient.SendAsync(payload, payload.Length, targets[targetIndex]);
            }
        }

        Dictionary<string, IPAddress> candidates = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<UdpReceiveResult> receiveTask = udpClient.ReceiveAsync();
            Task delayTask = Task.Delay(300, cancellationToken);
            Task completed = await Task.WhenAny(receiveTask, delayTask);
            if (completed != receiveTask)
            {
                continue;
            }

            UdpReceiveResult result = receiveTask.Result;
            string response = Encoding.UTF8.GetString(result.Buffer);
            if (response.StartsWith(DiscoveryResponsePrefix, StringComparison.Ordinal))
            {
                string[] parts = response.Split(':');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int responsePort) && responsePort == transferPort)
                {
                    IPAddress address = result.RemoteEndPoint.Address;
                    string ip = address.ToString();
                    if (!candidates.ContainsKey(ip))
                    {
                        candidates.Add(ip, address);
                        Log(log, "识别到候选接收端: " + ip);
                    }
                }
            }
        }

        if (candidates.Count > 0)
        {
            string selected = SelectBestReceiver(candidates.Values.ToList(), log);
            Log(log, "选择接收端: " + selected);
            return selected;
        }

        Log(log, "UDP IPv4 未识别到接收端，开始 IPv6 链路本地识别...");
        string? ipv6Result = await DiscoverByIPv6MulticastAsync(transferPort, log, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ipv6Result))
        {
            Log(log, "IPv6 链路本地识别到接收端: " + ipv6Result);
            return ipv6Result;
        }

        Log(log, "UDP 未识别到接收端，开始 TCP 同网段兜底扫描...");
        string? tcpResult = await DiscoverByTcpScanAsync(transferPort, log, cancellationToken);
        if (!string.IsNullOrWhiteSpace(tcpResult))
        {
            Log(log, "TCP 扫描识别到接收端: " + tcpResult);
            return tcpResult;
        }

        Log(log, "未识别到接收端，请确认接收端已点击开始接收，并允许防火墙访问");
        return null;
    }

    public static IEnumerable<string> GetLocalIPv4Addresses()
    {
        string hostName = Dns.GetHostName();
        IPAddress[] addresses = Dns.GetHostAddresses(hostName);
        for (int i = 0; i < addresses.Length; i++)
        {
            IPAddress address = addresses[i];
            if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            {
                yield return address.ToString();
            }
        }
    }

    public static int ParsePort(string value)
    {
        if (!int.TryParse(value, out int port) || port <= 0 || port > 65535)
        {
            throw new ArgumentException("端口非法: " + value);
        }

        return port;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.##") + " " + units[unit];
    }

    private static async Task SendParallelAsync(string host, int port, int parallelConnections, Action<BlockingCollection<SendItem>> produce, Action<string>? log, Action<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        parallelConnections = Math.Clamp(parallelConnections, 1, 32);
        BlockingCollection<SendItem> queue = new BlockingCollection<SendItem>(4096);
        long sentBytes = 0;

        Log(log, "连接 " + host + ":" + port + "，并行连接数: " + parallelConnections);
        Report(progress, 0, 0, "连接中...");

        Task producer = Task.Run(() =>
        {
            try
            {
                produce(queue);
            }
            finally
            {
                queue.CompleteAdding();
            }
        }, cancellationToken);

        Task[] workers = new Task[parallelConnections];
        for (int i = 0; i < workers.Length; i++)
        {
            int workerIndex = i + 1;
            workers[i] = Task.Run(async () =>
            {
                using TcpClient client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                using NetworkStream stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(Magic), cancellationToken);

                foreach (SendItem item in queue.GetConsumingEnumerable(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsDirectory)
                    {
                        await WriteEntryHeaderAsync(stream, EntryDirectory, item.RelativePath, cancellationToken);
                        Log(log, "[目录] " + item.RelativePath);
                        continue;
                    }

                    await TrySendFileAsync(stream, item.FullPath, item.RelativePath, item.SkipSystemFiles, log, progress, bytes =>
                    {
                        long total = Interlocked.Add(ref sentBytes, bytes);
                        Report(progress, total, 0, "已发送 " + FormatBytes(total));
                    }, cancellationToken);
                }

                await stream.WriteAsync(new[] { EntryEnd }, cancellationToken);
                Log(log, "worker " + workerIndex + " 完成");
            }, cancellationToken);
        }

        await producer;
        await Task.WhenAll(workers);
        await SendSessionEndAsync(host, port, cancellationToken);

        Log(log, "发送完成，共 " + FormatBytes(sentBytes));
        Report(progress, sentBytes, sentBytes, "发送完成: " + FormatBytes(sentBytes));
    }

    private static async Task SendSessionEndAsync(string host, int port, CancellationToken cancellationToken)
    {
        using TcpClient client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Magic), cancellationToken);
        await stream.WriteAsync(new[] { EntrySessionEnd }, cancellationToken);
    }

    private static async Task ReceiveWorkerAsync(NetworkStream stream, string outputDir, bool overwrite, byte firstEntryType, object pathLock, Action<string>? log, Action<TransferProgress>? progress, Func<long> incrementFiles, Action<long> addBytes, CancellationToken cancellationToken)
    {
        byte entryType = firstEntryType;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entryType == EntryEnd)
            {
                break;
            }

            string relativePath = await ReadStringAsync(stream, cancellationToken);
            string targetPath = ResolveInsideDirectory(outputDir, relativePath);

            if (entryType == EntryDirectory)
            {
                try
                {
                    Directory.CreateDirectory(targetPath);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PathTooLongException)
                {
                    Log(log, "[跳过目录] " + relativePath + "，原因: " + ex.Message);
                }
            }
            else if (entryType == EntryFile)
            {
                long length = await ReadInt64Async(stream, cancellationToken);
                bool canWrite = true;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? outputDir);
                    lock (pathLock)
                    {
                        if (File.Exists(targetPath) && !overwrite)
                        {
                            targetPath = GetNonConflictPath(targetPath);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PathTooLongException)
                {
                    canWrite = false;
                    Log(log, "[跳过文件] " + relativePath + "，原因: " + ex.Message);
                }

                if (canWrite)
                {
                    await ReceiveFileAsync(stream, targetPath, length, log, progress, addBytes, cancellationToken);
                }
                else
                {
                    await DrainFileAsync(stream, length, progress, addBytes, cancellationToken);
                }

                incrementFiles();
            }
            else
            {
                throw new InvalidDataException("未知条目类型: " + entryType);
            }

            entryType = (await ReadExactAsync(stream, 1, cancellationToken))[0];
        }
    }

    private static void AddDirectoryTree(BlockingCollection<SendItem> queue, string directoryPath, string relativeDir, bool skipSystemFiles, Action<string>? log, CancellationToken cancellationToken)
    {
        foreach (string directory in EnumerateDirectories(directoryPath, log))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipSystemFiles && ShouldSkipSystemPath(directory))
            {
                Log(log, "[跳过系统目录] " + directory);
                continue;
            }

            string childRelativeDir = CombineRelative(relativeDir, Path.GetFileName(directory));
            queue.Add(SendItem.Directory(directory, childRelativeDir, skipSystemFiles), cancellationToken);
            AddDirectoryTree(queue, directory, childRelativeDir, skipSystemFiles, log, cancellationToken);
        }

        foreach (string file in EnumerateFiles(directoryPath, log))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skipSystemFiles && ShouldSkipSystemPath(file))
            {
                Log(log, "[跳过系统文件] " + file);
                continue;
            }

            queue.Add(SendItem.File(file, CombineRelative(relativeDir, Path.GetFileName(file)), skipSystemFiles), cancellationToken);
        }
    }

    private static async Task RunDiscoveryResponderAsync(int transferPort, Action<string>? log, CancellationToken cancellationToken)
    {
        Task ipv4Task = RunIPv4DiscoveryResponderAsync(transferPort, log, cancellationToken);
        Task ipv6Task = RunIPv6DiscoveryResponderAsync(transferPort, log, cancellationToken);

        try
        {
            await Task.WhenAll(ipv4Task, ipv6Task);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(log, "自动识别监听异常: " + ex.Message);
        }
    }

    private static async Task RunIPv4DiscoveryResponderAsync(int transferPort, Action<string>? log, CancellationToken cancellationToken)
    {
        using UdpClient udpClient = new UdpClient(AddressFamily.InterNetwork);
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        byte[] response = Encoding.UTF8.GetBytes(BuildDiscoveryResponse(transferPort));

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log(log, "自动识别监听失败: " + ex.Message);
                break;
            }

            string request = Encoding.UTF8.GetString(result.Buffer);
            if (!request.StartsWith(DiscoveryRequest, StringComparison.Ordinal))
            {
                continue;
            }

            IPEndPoint target = new IPEndPoint(result.RemoteEndPoint.Address, result.RemoteEndPoint.Port);
            await udpClient.SendAsync(response, response.Length, target);
        }
    }

    private static async Task RunIPv6DiscoveryResponderAsync(int transferPort, Action<string>? log, CancellationToken cancellationToken)
    {
        List<long> indexes = GetIPv6MulticastInterfaceIndexes(log);
        if (indexes.Count == 0)
        {
            Log(log, "没有可用的 IPv6 链路本地网卡，跳过 IPv6 自动识别");
            return;
        }

        using UdpClient udpClient = new UdpClient(AddressFamily.InterNetworkV6);
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udpClient.Client.DualMode = false;
        udpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, DiscoveryPort));

        IPAddress multicastAddress = IPAddress.Parse("ff02::1");
        for (int i = 0; i < indexes.Count; i++)
        {
            try
            {
                udpClient.JoinMulticastGroup((int)indexes[i], multicastAddress);
            }
            catch (Exception ex)
            {
                Log(log, "加入 IPv6 自动识别组失败: 网卡 " + indexes[i] + "，" + ex.Message);
            }
        }

        byte[] response = Encoding.UTF8.GetBytes(BuildDiscoveryResponse(transferPort));
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log(log, "IPv6 自动识别接收失败: " + ex.Message);
                continue;
            }

            string request = Encoding.UTF8.GetString(result.Buffer);
            if (!request.StartsWith(DiscoveryRequest, StringComparison.Ordinal))
            {
                continue;
            }

            await udpClient.SendAsync(response, response.Length, result.RemoteEndPoint);
        }
    }

    private static List<IPEndPoint> GetDiscoveryTargets(Action<string>? log)
    {
        List<IPEndPoint> targets = new List<IPEndPoint>
        {
            new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)
        };

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface networkInterface = interfaces[i];
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                foreach (UnicastIPAddressInformation addressInfo in properties.UnicastAddresses)
                {
                    if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (IPAddress.IsLoopback(addressInfo.Address) || addressInfo.IPv4Mask == null)
                    {
                        continue;
                    }

                    IPAddress broadcast = GetBroadcastAddress(addressInfo.Address, addressInfo.IPv4Mask);
                    IPEndPoint endpoint = new IPEndPoint(broadcast, DiscoveryPort);
                    if (!ContainsEndpoint(targets, endpoint))
                    {
                        targets.Add(endpoint);
                        Log(log, "自动识别广播地址: " + endpoint.Address + " (" + networkInterface.Name + ")");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log(log, "枚举网卡广播地址失败，继续使用全局广播: " + ex.Message);
        }

        return targets;
    }

    private static string SelectBestReceiver(List<IPAddress> candidates, Action<string>? log)
    {
        List<LocalIPv4Info> localAddresses = GetLocalIPv4Infos(log);
        IPAddress bestAddress = candidates[0];
        int bestScore = int.MinValue;

        for (int i = 0; i < candidates.Count; i++)
        {
            IPAddress candidate = candidates[i];
            int score = ScoreCandidate(candidate, localAddresses);
            Log(log, "候选 " + candidate + " 评分: " + score);
            if (score > bestScore)
            {
                bestScore = score;
                bestAddress = candidate;
            }
        }

        return bestAddress.ToString();
    }

    private static async Task<string?> DiscoverByIPv6MulticastAsync(int transferPort, Action<string>? log, CancellationToken cancellationToken)
    {
        List<long> indexes = GetIPv6MulticastInterfaceIndexes(log);
        if (indexes.Count == 0)
        {
            Log(log, "没有可用的 IPv6 链路本地网卡");
            return null;
        }

        byte[] payload = Encoding.UTF8.GetBytes(DiscoveryRequest + ":" + transferPort);
        using UdpClient udpClient = new UdpClient(AddressFamily.InterNetworkV6);
        udpClient.Client.DualMode = false;
        udpClient.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));

        IPAddress multicastAddress = IPAddress.Parse("ff02::1");
        for (int i = 0; i < indexes.Count; i++)
        {
            IPEndPoint target = new IPEndPoint(multicastAddress, DiscoveryPort);
            target.Address.ScopeId = indexes[i];
            try
            {
                await udpClient.SendAsync(payload, payload.Length, target);
                Log(log, "IPv6 自动识别已发送到网卡: " + indexes[i]);
            }
            catch (Exception ex)
            {
                Log(log, "IPv6 自动识别发送失败: 网卡 " + indexes[i] + "，" + ex.Message);
            }
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task<UdpReceiveResult> receiveTask = udpClient.ReceiveAsync();
            Task delayTask = Task.Delay(250, cancellationToken);
            Task completed = await Task.WhenAny(receiveTask, delayTask);
            if (completed != receiveTask)
            {
                continue;
            }

            UdpReceiveResult result = receiveTask.Result;
            string response = Encoding.UTF8.GetString(result.Buffer);
            if (!TryParseDiscoveryResponse(response, transferPort))
            {
                continue;
            }

            IPAddress address = result.RemoteEndPoint.Address;
            if (address.IsIPv6LinkLocal && address.ScopeId == 0)
            {
                address.ScopeId = result.RemoteEndPoint.Address.ScopeId;
            }

            return address.ToString();
        }

        return null;
    }

    private static async Task<string?> DiscoverByTcpScanAsync(int transferPort, Action<string>? log, CancellationToken cancellationToken)
    {
        List<LocalIPv4Info> localAddresses = GetLocalIPv4Infos(log);
        localAddresses.Sort((a, b) => GetLocalScanPriority(b).CompareTo(GetLocalScanPriority(a)));

        HashSet<string> scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < localAddresses.Count; i++)
        {
            LocalIPv4Info local = localAddresses[i];
            List<IPAddress> targets = BuildScanTargets(local);
            Log(log, "TCP 扫描网卡 " + local.Address + "，候选数: " + targets.Count);
            string? result = await ScanTcpTargetsAsync(targets, scanned, local.Address, transferPort, cancellationToken);
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        List<IPAddress> directTargets = BuildFixedDirectSubnetTargets();
        Log(log, "TCP 固定直连网段扫描 192.168.10.1-254，候选数: " + directTargets.Count);
        string? directResult = await ScanTcpTargetsAsync(directTargets, scanned, null, transferPort, cancellationToken);
        if (!string.IsNullOrWhiteSpace(directResult))
        {
            return directResult;
        }

        return null;
    }

    private static async Task<string?> ScanTcpTargetsAsync(List<IPAddress> targets, HashSet<string> scanned, IPAddress? skipAddress, int transferPort, CancellationToken cancellationToken)
    {
        using SemaphoreSlim semaphore = new SemaphoreSlim(64);
        using CancellationTokenSource foundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<string?>> tasks = new List<Task<string?>>();

        for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            IPAddress target = targets[targetIndex];
            string targetText = target.ToString();
            if (!scanned.Add(targetText) || (skipAddress != null && target.Equals(skipAddress)))
            {
                continue;
            }

            await semaphore.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (foundCts.IsCancellationRequested)
                    {
                        return null;
                    }

                    bool ok = await ProbeTcpAsync(target, transferPort, foundCts.Token);
                    if (ok)
                    {
                        foundCts.Cancel();
                        return targetText;
                    }

                    return null;
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        while (tasks.Count > 0)
        {
            Task<string?> finished = await Task.WhenAny(tasks);
            tasks.Remove(finished);
            string? result = await finished;
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }
        }

        return null;
    }

    private static int GetLocalScanPriority(LocalIPv4Info local)
    {
        int score = 0;
        if (local.InterfaceType == NetworkInterfaceType.Ethernet || local.InterfaceType == NetworkInterfaceType.GigabitEthernet || local.InterfaceType == NetworkInterfaceType.FastEthernetFx || local.InterfaceType == NetworkInterfaceType.FastEthernetT)
        {
            score += 100;
        }

        if (!local.HasGateway)
        {
            score += 80;
        }

        return score;
    }

    private static List<IPAddress> BuildScanTargets(LocalIPv4Info local)
    {
        if (IsIPv4LinkLocal(local.Address))
        {
            return BuildLinkLocalScanTargets(local);
        }

        byte[] addressBytes = local.Address.GetAddressBytes();
        byte[] maskBytes = local.Mask.GetAddressBytes();
        byte[] networkBytes = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            networkBytes[i] = (byte)(addressBytes[i] & maskBytes[i]);
        }

        List<IPAddress> targets = new List<IPAddress>();
        for (int host = 1; host <= 254; host++)
        {
            byte[] targetBytes = (byte[])networkBytes.Clone();
            targetBytes[3] = (byte)host;
            targets.Add(new IPAddress(targetBytes));
        }

        return targets;
    }

    private static List<IPAddress> BuildFixedDirectSubnetTargets()
    {
        List<IPAddress> targets = new List<IPAddress>();
        for (int host = 1; host <= 254; host++)
        {
            targets.Add(new IPAddress(new byte[] { 192, 168, 10, (byte)host }));
        }

        return targets;
    }

    private static List<IPAddress> BuildLinkLocalScanTargets(LocalIPv4Info local)
    {
        List<IPAddress> targets = new List<IPAddress>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNeighborLinkLocalTargets(targets, seen, local);

        byte[] addressBytes = local.Address.GetAddressBytes();
        int third = addressBytes[2];
        for (int offset = -2; offset <= 2; offset++)
        {
            int segment = third + offset;
            if (segment < 0 || segment > 255)
            {
                continue;
            }

            AddLinkLocalSegmentTargets(targets, seen, segment);
        }

        return targets;
    }

    private static void AddLinkLocalSegmentTargets(List<IPAddress> targets, HashSet<string> seen, int thirdSegment)
    {
        for (int host = 1; host <= 254; host++)
        {
            AddTarget(targets, seen, new IPAddress(new byte[] { 169, 254, (byte)thirdSegment, (byte)host }));
        }
    }

    private static void AddNeighborLinkLocalTargets(List<IPAddress> targets, HashSet<string> seen, LocalIPv4Info local)
    {
        try
        {
            using Process process = new Process();
            process.StartInfo.FileName = "arp";
            process.StartInfo.Arguments = "-a";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] parts = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !IPAddress.TryParse(parts[0], out IPAddress? address))
                {
                    continue;
                }

                if (address.Equals(local.Address) || !IsIPv4LinkLocal(address))
                {
                    continue;
                }

                AddTarget(targets, seen, address);
            }
        }
        catch
        {
        }
    }

    private static void AddTarget(List<IPAddress> targets, HashSet<string> seen, IPAddress target)
    {
        if (seen.Add(target.ToString()))
        {
            targets.Add(target);
        }
    }

    private static string BuildDiscoveryResponse(int transferPort)
    {
        return DiscoveryResponsePrefix + transferPort;
    }

    private static bool TryParseDiscoveryResponse(string response, int transferPort)
    {
        if (!response.StartsWith(DiscoveryResponsePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = response.Split(':');
        return parts.Length >= 2 && int.TryParse(parts[1], out int responsePort) && responsePort == transferPort;
    }

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static List<long> GetIPv6MulticastInterfaceIndexes(Action<string>? log)
    {
        List<long> indexes = new List<long>();
        HashSet<long> seen = new HashSet<long>();
        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface networkInterface = interfaces[i];
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                IPv6InterfaceProperties? ipv6Properties;
                try
                {
                    ipv6Properties = properties.GetIPv6Properties();
                }
                catch
                {
                    ipv6Properties = null;
                }

                if (ipv6Properties == null)
                {
                    continue;
                }

                bool hasLinkLocal = false;
                foreach (UnicastIPAddressInformation addressInfo in properties.UnicastAddresses)
                {
                    if (addressInfo.Address.AddressFamily == AddressFamily.InterNetworkV6 && addressInfo.Address.IsIPv6LinkLocal)
                    {
                        hasLinkLocal = true;
                        break;
                    }
                }

                long index = ipv6Properties.Index;
                if (hasLinkLocal && seen.Add(index))
                {
                    indexes.Add(index);
                    Log(log, "IPv6 自动识别网卡: " + networkInterface.Name + "，索引: " + index);
                }
            }
        }
        catch (Exception ex)
        {
            Log(log, "获取 IPv6 网卡信息失败: " + ex.Message);
        }

        return indexes;
    }

    private static async Task<bool> ProbeTcpAsync(IPAddress target, int port, CancellationToken cancellationToken)
    {
        using TcpClient client = new TcpClient();
        Task connectTask = client.ConnectAsync(target, port);
        Task timeoutTask = Task.Delay(500, cancellationToken);
        Task completed = await Task.WhenAny(connectTask, timeoutTask);
        if (completed != connectTask)
        {
            return false;
        }

        try
        {
            await connectTask;
        }
        catch
        {
            return false;
        }

        if (!client.Connected)
        {
            return false;
        }

        using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes(ProbeRequest);
        await stream.WriteAsync(request, cancellationToken);

        Task<byte[]> readTask = ReadExactAsync(stream, ProbeResponse.Length, cancellationToken);
        Task readTimeoutTask = Task.Delay(500, cancellationToken);
        Task readCompleted = await Task.WhenAny(readTask, readTimeoutTask);
        if (readCompleted != readTask)
        {
            return false;
        }

        try
        {
            string response = Encoding.ASCII.GetString(await readTask);
            return response == ProbeResponse;
        }
        catch
        {
            return false;
        }
    }

    private static int ScoreCandidate(IPAddress candidate, List<LocalIPv4Info> localAddresses)
    {
        int bestScore = 0;
        for (int i = 0; i < localAddresses.Count; i++)
        {
            LocalIPv4Info local = localAddresses[i];
            if (!IsSameSubnet(candidate, local.Address, local.Mask))
            {
                continue;
            }

            int score = 100;
            if (local.InterfaceType == NetworkInterfaceType.Ethernet || local.InterfaceType == NetworkInterfaceType.GigabitEthernet || local.InterfaceType == NetworkInterfaceType.FastEthernetFx || local.InterfaceType == NetworkInterfaceType.FastEthernetT)
            {
                score += 100;
            }

            if (!local.HasGateway)
            {
                score += 80;
            }

            if (!local.HasDnsSuffix)
            {
                score += 10;
            }

            if (score > bestScore)
            {
                bestScore = score;
            }
        }

        return bestScore;
    }

    private static List<LocalIPv4Info> GetLocalIPv4Infos(Action<string>? log)
    {
        List<LocalIPv4Info> infos = new List<LocalIPv4Info>();
        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface networkInterface = interfaces[i];
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                IPInterfaceProperties properties = networkInterface.GetIPProperties();
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any))
                    {
                        hasGateway = true;
                        break;
                    }
                }

                foreach (UnicastIPAddressInformation addressInfo in properties.UnicastAddresses)
                {
                    if (addressInfo.Address.AddressFamily != AddressFamily.InterNetwork || addressInfo.IPv4Mask == null || IPAddress.IsLoopback(addressInfo.Address))
                    {
                        continue;
                    }

                    infos.Add(new LocalIPv4Info(addressInfo.Address, addressInfo.IPv4Mask, networkInterface.NetworkInterfaceType, hasGateway, !string.IsNullOrWhiteSpace(properties.DnsSuffix)));
                }
            }
        }
        catch (Exception ex)
        {
            Log(log, "获取本机网卡信息失败: " + ex.Message);
        }

        return infos;
    }

    private static bool IsSameSubnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        byte[] leftBytes = left.GetAddressBytes();
        byte[] rightBytes = right.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        if (leftBytes.Length != rightBytes.Length || leftBytes.Length != maskBytes.Length)
        {
            return false;
        }

        for (int i = 0; i < leftBytes.Length; i++)
        {
            if ((leftBytes[i] & maskBytes[i]) != (rightBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        byte[] addressBytes = address.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();
        byte[] broadcastBytes = new byte[addressBytes.Length];

        for (int i = 0; i < broadcastBytes.Length; i++)
        {
            broadcastBytes[i] = (byte)(addressBytes[i] | ~maskBytes[i]);
        }

        return new IPAddress(broadcastBytes);
    }

    private static bool ContainsEndpoint(List<IPEndPoint> endpoints, IPEndPoint endpoint)
    {
        for (int i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i].Address.Equals(endpoint.Address) && endpoints[i].Port == endpoint.Port)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task TrySendFileAsync(NetworkStream stream, string path, string relativePath, bool skipSystemFiles, Action<string>? log, Action<TransferProgress>? progress, Action<long> addBytes, CancellationToken cancellationToken)
    {
        if (skipSystemFiles && ShouldSkipSystemPath(path))
        {
            Log(log, "[跳过系统文件] " + path);
            return;
        }

        FileInfo fileInfo;
        FileStream fileStream;
        try
        {
            fileInfo = new FileInfo(path);
            fileStream = OpenReadShared(fileInfo.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PathTooLongException)
        {
            Log(log, "[跳过文件] " + path + "，原因: " + ex.Message);
            return;
        }

        await using (fileStream)
        {
            await WriteEntryHeaderAsync(stream, EntryFile, relativePath, cancellationToken);
            await WriteInt64Async(stream, fileInfo.Length, cancellationToken);
            await SendFileAsync(stream, fileStream, fileInfo.Length, path, log, progress, addBytes, cancellationToken);
        }
    }

    private static async Task WriteEntryHeaderAsync(NetworkStream stream, byte entryType, string relativePath, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new[] { entryType }, cancellationToken);
        await WriteStringAsync(stream, relativePath, cancellationToken);
    }

    private static bool IsRecoverableTransferItemException(Exception ex)
    {
        return ex is UnauthorizedAccessException
            || ex is PathTooLongException
            || ex is FileNotFoundException
            || ex is DirectoryNotFoundException
            || ex is IOException;
    }

    private static string GetUniqueTopName(Dictionary<string, int> usedNames, string name)
    {
        if (!usedNames.TryGetValue(name, out int count))
        {
            usedNames.Add(name, 1);
            return name;
        }

        usedNames[name] = count + 1;

        string extension = Path.GetExtension(name);
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(nameWithoutExtension))
        {
            nameWithoutExtension = name;
            extension = string.Empty;
        }

        return nameWithoutExtension + " (" + count + ")" + extension;
    }

    private static string GetDriveRootName(string path)
    {
        string root = Path.GetPathRoot(path) ?? "root";
        string name = root.Replace(":\\", "_Drive").Replace(":/", "_Drive").Trim('\\', '/');
        if (string.IsNullOrWhiteSpace(name))
        {
            return "root";
        }

        return name;
    }

    private static string CombineRelative(string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return right;
        }

        return left.TrimEnd('/', '\\') + "/" + right.TrimStart('/', '\\');
    }

    private static async Task SendFileAsync(Stream stream, FileStream fileStream, long length, string path, Action<string>? log, Action<TransferProgress>? progress, Action<long> addBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long sent = 0;
        while (sent < length)
        {
            int request = (int)Math.Min(buffer.Length, length - sent);
            int read;
            try
            {
                read = await fileStream.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
            }
            catch (Exception ex) when (IsRecoverableTransferItemException(ex))
            {
                Log(log, "[文件读取中断，补齐占位数据保持连接] " + path + "，原因: " + ex.Message);
                break;
            }

            if (read <= 0)
            {
                break;
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sent += read;
            addBytes(read);
        }

        if (sent < length)
        {
            await FillRemainingBytesAsync(stream, length - sent, cancellationToken);
        }
    }

    private static async Task FillRemainingBytesAsync(Stream stream, long remaining, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        while (remaining > 0)
        {
            int write = (int)Math.Min(buffer.Length, remaining);
            await stream.WriteAsync(buffer.AsMemory(0, write), cancellationToken);
            remaining -= write;
        }
    }

    private static async Task ReceiveFileAsync(Stream stream, string path, long length, Action<string>? log, Action<TransferProgress>? progress, Action<long> addBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long remaining = length;

        FileStream? fileStream = null;
        try
        {
            fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        }
        catch (Exception ex) when (IsRecoverableTransferItemException(ex))
        {
            Log(log, "[跳过文件] " + path + "，原因: " + ex.Message);
            await DrainFileAsync(stream, length, progress, addBytes, cancellationToken);
            return;
        }

        await using (fileStream)
        {
            bool canWrite = true;
            while (remaining > 0)
            {
                int request = (int)Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
                if (read <= 0)
                {
                    throw new EndOfStreamException("连接提前断开");
                }

                if (canWrite)
                {
                    try
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    catch (Exception ex) when (IsRecoverableTransferItemException(ex))
                    {
                        canWrite = false;
                        Log(log, "[接收写入中断，继续排空数据] " + path + "，原因: " + ex.Message);
                    }
                }

                remaining -= read;
                addBytes(read);
            }
        }
    }

    private static async Task DrainFileAsync(Stream stream, long length, Action<TransferProgress>? progress, Action<long> addBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long remaining = length;

        while (remaining > 0)
        {
            int request = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException("连接提前断开");
            }

            remaining -= read;
            addBytes(read);
        }
    }

    private static async Task<long> ReceiveSpeedTestAsync(Stream stream, Action<TransferProgress>? progress, Action<long> addBytes, CancellationToken cancellationToken)
    {
        long remaining = await ReadInt64Async(stream, cancellationToken);
        long total = remaining;
        byte[] buffer = new byte[BufferSize];

        while (remaining > 0)
        {
            int request = (int)Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException("测速连接提前断开");
            }

            remaining -= read;
            addBytes(read);
        }

        return total;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read <= 0)
            {
                throw new EndOfStreamException("连接提前断开");
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task<long> ReadInt64Async(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = await ReadExactAsync(stream, 8, cancellationToken);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static async Task WriteInt64Async(Stream stream, long value, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, cancellationToken);
    }

    private static async Task<string> ReadStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        long length = await ReadInt64Async(stream, cancellationToken);
        if (length < 0 || length > 1024 * 1024)
        {
            throw new InvalidDataException("路径长度非法: " + length);
        }

        byte[] buffer = await ReadExactAsync(stream, (int)length, cancellationToken);
        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task WriteStringAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(value);
        await WriteInt64Async(stream, buffer.Length, cancellationToken);
        await stream.WriteAsync(buffer, cancellationToken);
    }

    private static string ResolveInsideDirectory(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("空路径非法");
        }

        string normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("不允许绝对路径: " + relativePath);
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        string fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("路径越界: " + relativePath);
        }

        return fullPath;
    }

    private static string GetNonConflictPath(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        int index = 1;
        while (true)
        {
            string candidate = Path.Combine(directory, name + " (" + index + ")" + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string directoryPath, Action<string>? log)
    {
        try
        {
            string[] directories = Directory.GetDirectories(directoryPath);
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            return directories;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PathTooLongException)
        {
            Log(log, "[跳过目录] " + directoryPath + "，原因: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directoryPath, Action<string>? log)
    {
        try
        {
            string[] files = Directory.GetFiles(directoryPath);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PathTooLongException)
        {
            Log(log, "[跳过文件列表] " + directoryPath + "，原因: " + ex.Message);
            return Array.Empty<string>();
        }
    }

    private static FileStream OpenReadShared(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, true);
    }

    private static bool ShouldSkipSystemPath(string path)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(fullPath);
        if (string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Recovery", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Config.Msi", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "WindowsApps", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Windows", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "ProgramData", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Documents and Settings", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "swapfile.sys", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "pagefile.sys", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "hiberfil.sys", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.IndexOf(Path.DirectorySeparatorChar + "$RECYCLE.BIN" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || fullPath.IndexOf(Path.DirectorySeparatorChar + "System Volume Information" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || fullPath.IndexOf(Path.DirectorySeparatorChar + "WindowsApps" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || fullPath.IndexOf(Path.DirectorySeparatorChar + "Windows" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || fullPath.IndexOf(Path.DirectorySeparatorChar + "ProgramData" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
            || fullPath.IndexOf(Path.DirectorySeparatorChar + "Documents and Settings" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void Log(Action<string>? log, string message)
    {
        log?.Invoke(message);
    }

    private static void Report(Action<TransferProgress>? progress, long transferredBytes, long totalBytes, string message)
    {
        progress?.Invoke(new TransferProgress(transferredBytes, totalBytes, message));
    }
}

internal readonly struct TransferProgress
{
    public TransferProgress(long transferredBytes, long totalBytes, string message)
    {
        TransferredBytes = transferredBytes;
        TotalBytes = totalBytes;
        Message = message;
    }

    public long TransferredBytes { get; }

    public long TotalBytes { get; }

    public string Message { get; }
}

internal readonly struct LocalIPv4Info
{
    public LocalIPv4Info(IPAddress address, IPAddress mask, NetworkInterfaceType interfaceType, bool hasGateway, bool hasDnsSuffix)
    {
        Address = address;
        Mask = mask;
        InterfaceType = interfaceType;
        HasGateway = hasGateway;
        HasDnsSuffix = hasDnsSuffix;
    }

    public IPAddress Address { get; }

    public IPAddress Mask { get; }

    public NetworkInterfaceType InterfaceType { get; }

    public bool HasGateway { get; }

    public bool HasDnsSuffix { get; }
}

internal readonly struct SendItem
{
    private SendItem(string fullPath, string relativePath, bool isDirectory, bool skipSystemFiles)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        IsDirectory = isDirectory;
        SkipSystemFiles = skipSystemFiles;
    }

    public string FullPath { get; }

    public string RelativePath { get; }

    public bool IsDirectory { get; }

    public bool SkipSystemFiles { get; }

    public static SendItem Directory(string fullPath, string relativePath, bool skipSystemFiles)
    {
        return new SendItem(fullPath, relativePath, true, skipSystemFiles);
    }

    public static SendItem File(string fullPath, string relativePath, bool skipSystemFiles)
    {
        return new SendItem(fullPath, relativePath, false, skipSystemFiles);
    }
}
