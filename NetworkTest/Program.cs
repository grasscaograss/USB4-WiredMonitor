using System.Net;
using System.Net.Sockets;

namespace NetworkTest;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  NetworkTest server [port]          - 启动吞吐量测试服务端");
            Console.WriteLine("  NetworkTest client <host> [port]    - 启动吞吐量测试客户端");
            Console.WriteLine("  NetworkTest ping <host> [port]      - 测量延迟");
            Console.WriteLine("  NetworkTest discovery               - 发现 Thunderbolt 网络接口");
            return;
        }

        var mode = args[0].ToLower();
        var port = args.Length > 2 ? int.Parse(args[2]) : 9800;
        var host = args.Length > 1 ? args[1] : "localhost";

        switch (mode)
        {
            case "server":
                await RunServer(port);
                break;
            case "client":
                await RunClient(host, port);
                break;
            case "ping":
                await RunPingTest(host, port);
                break;
            case "discovery":
                DiscoverInterfaces();
                break;
            default:
                Console.WriteLine($"未知模式: {mode}");
                break;
        }
    }

    static async Task RunServer(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[服务端] 监听端口 {port}，等待连接...");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClient(client));
        }
    }

    static async Task HandleClient(TcpClient client)
    {
        var endpoint = client.Client.RemoteEndPoint;
        Console.WriteLine($"[服务端] 客户端已连接: {endpoint}");

        using var stream = client.GetStream();
        var buffer = new byte[1024 * 1024]; // 1MB buffer

        // 接收模式指令
        var modeBuffer = new byte[1];
        await stream.ReadAsync(modeBuffer);
        var mode = (TestMode)modeBuffer[0];

        switch (mode)
        {
            case TestMode.ThroughputUpload:
                // 客户端发送数据，服务端接收
                await ReceiveThroughput(stream, buffer);
                break;
            case TestMode.ThroughputDownload:
                // 服务端发送数据，客户端接收
                await SendThroughput(stream, buffer);
                break;
            case TestMode.Latency:
                await HandleLatencyTest(stream);
                break;
        }

        client.Close();
        Console.WriteLine($"[服务端] 客户端断开: {endpoint}");
    }

    static async Task SendThroughput(NetworkStream stream, byte[] buffer)
    {
        // 填充随机数据
        Random.Shared.NextBytes(buffer);

        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromSeconds(10);
        var reportInterval = TimeSpan.FromSeconds(1);
        var lastReport = startTime;

        Console.WriteLine("[服务端] 开始发送数据 (10秒)...");

        while (DateTime.UtcNow - startTime < duration)
        {
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            totalBytes += buffer.Length;

            if (DateTime.UtcNow - lastReport >= reportInterval)
            {
                var elapsed = (DateTime.UtcNow - lastReport).TotalSeconds;
                var speed = (totalBytes / (DateTime.UtcNow - startTime).TotalSeconds) / (1024.0 * 1024.0);
                Console.WriteLine($"[服务端] 已发送 {totalBytes / (1024.0 * 1024.0):F1} MB, 速度: {speed:F1} MB/s");
                lastReport = DateTime.UtcNow;
            }
        }

        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var avgSpeed = (totalBytes / totalTime) / (1024.0 * 1024.0);
        Console.WriteLine($"[服务端] 完成! 总计 {totalBytes / (1024.0 * 1024.0):F1} MB, 平均速度: {avgSpeed:F1} MB/s");
    }

    static async Task ReceiveThroughput(NetworkStream stream, byte[] buffer)
    {
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var lastReport = startTime;

        Console.WriteLine("[服务端] 开始接收数据 (10秒)...");

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            totalBytes += bytesRead;

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var speed = (totalBytes / elapsed) / (1024.0 * 1024.0);
                Console.WriteLine($"[服务端] 已接收 {totalBytes / (1024.0 * 1024.0):F1} MB, 速度: {speed:F1} MB/s");
                lastReport = DateTime.UtcNow;
            }
        }

        var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;
        var avgSpeed = (totalBytes / totalTime) / (1024.0 * 1024.0);
        Console.WriteLine($"[服务端] 完成! 总计 {totalBytes / (1024.0 * 1024.0):F1} MB, 平均速度: {avgSpeed:F1} MB/s");
    }

    static async Task HandleLatencyTest(NetworkStream stream)
    {
        var pingBuffer = new byte[64];
        int received = 0;

        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(pingBuffer.AsMemory(0, 64));
                if (bytesRead == 0) break;
            }
            catch
            {
                break;
            }

            // 立即回传
            await stream.WriteAsync(pingBuffer.AsMemory(0, bytesRead));
            received++;
        }

        Console.WriteLine($"[服务端] 延迟测试完成，处理 {received} 个 ping 包");
    }

    static async Task RunClient(string host, int port)
    {
        Console.WriteLine($"[客户端] 连接到 {host}:{port}...");
        using var client = new TcpClient();
        await client.ConnectAsync(host, port);

        using var stream = client.GetStream();
        var buffer = new byte[1024 * 1024];

        // 测试上传速度
        Console.WriteLine("\n=== 上传速度测试 (Windows → Mac) ===");
        await stream.WriteAsync(new byte[] { (byte)TestMode.ThroughputUpload });
        await RunUploadTest(stream, buffer);

        // 断开重连测试下载
        client.Close();

        Console.WriteLine($"\n[客户端] 重新连接测试下载速度...");
        using var client2 = new TcpClient();
        await client2.ConnectAsync(host, port);
        using var stream2 = client2.GetStream();

        Console.WriteLine("\n=== 下载速度测试 (Mac → Windows) ===");
        await stream2.WriteAsync(new byte[] { (byte)TestMode.ThroughputDownload });
        await RunDownloadTest(stream2, buffer);
    }

    static async Task RunUploadTest(NetworkStream stream, byte[] buffer)
    {
        Random.Shared.NextBytes(buffer);

        long totalBytes = 0;
        var startTime = DateTime.UtcNow;
        var duration = TimeSpan.FromSeconds(10);
        var lastReport = startTime;

        while (DateTime.UtcNow - startTime < duration)
        {
            await stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
            totalBytes += buffer.Length;

            if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(1))
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                var speed = (totalBytes / elapsed) / (1024.0 * 1024.0);
                Console.WriteLine($"[客户端] 上传: {totalBytes / (1024.0 * 1024.0):F1} MB, 速度: {speed:F1} MB/s");
                lastReport = DateTime.UtcNow;
            }
        }

        var avgSpeed = (totalBytes / (DateTime.UtcNow - startTime).TotalSeconds) / (1024.0 * 1024.0);
        Console.WriteLine($"[客户端] 上传完成! 平均速度: {avgSpeed:F1} MB/s");
    }

    static async Task RunDownloadTest(NetworkStream stream, byte[] buffer)
    {
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
        {
            totalBytes += bytesRead;

            if (DateTime.UtcNow - startTime >= TimeSpan.FromSeconds(10))
                break;

            var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
            if (elapsed >= 1 && totalBytes > 0)
            {
                var speed = (totalBytes / elapsed) / (1024.0 * 1024.0);
                Console.WriteLine($"[客户端] 下载: {totalBytes / (1024.0 * 1024.0):F1} MB, 速度: {speed:F1} MB/s");
            }
        }

        var avgSpeed = (totalBytes / (DateTime.UtcNow - startTime).TotalSeconds) / (1024.0 * 1024.0);
        Console.WriteLine($"[客户端] 下载完成! 平均速度: {avgSpeed:F1} MB/s");
    }

    static async Task RunPingTest(string host, int port)
    {
        Console.WriteLine($"[Ping] 连接到 {host}:{port}...");

        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { (byte)TestMode.Latency });

        var pingData = new byte[64];
        var pongBuffer = new byte[64];
        Random.Shared.NextBytes(pingData);

        var latencies = new List<double>();
        Console.WriteLine("[Ping] 发送 100 个 ping 包...\n");

        for (int i = 0; i < 100; i++)
        {
            BitConverter.GetBytes(i).CopyTo(pingData, 0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await stream.WriteAsync(pingData);
            await stream.ReadExactlyAsync(pongBuffer);
            sw.Stop();

            var latencyUs = sw.Elapsed.TotalMicroseconds;
            latencies.Add(latencyUs);

            if (i % 10 == 0)
                Console.WriteLine($"  ping {i}: {latencyUs:F0} μs ({latencyUs / 1000.0:F2} ms)");
        }

        Console.WriteLine($"\n[Ping] 结果:");
        Console.WriteLine($"  最小: {latencies.Min():F0} μs");
        Console.WriteLine($"  最大: {latencies.Max():F0} μs");
        Console.WriteLine($"  平均: {latencies.Average():F0} μs");
        Console.WriteLine($"  中位: {latencies.OrderBy(x => x).Skip(50).First():F0} μs");
        Console.WriteLine($"  P95:  {latencies.OrderBy(x => x).Skip(94).First():F0} μs");
        Console.WriteLine($"  P99:  {latencies.OrderBy(x => x).Skip(98).First():F0} μs");
    }

    static void DiscoverInterfaces()
    {
        Console.WriteLine("[发现] 网络接口列表:\n");

        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            var ipProps = ni.GetIPProperties();
            var ipv4 = ipProps.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            var isThunderbolt = ni.Name.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
                               ni.Description.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase) ||
                               ni.Name.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                               ni.Description.Contains("USB", StringComparison.OrdinalIgnoreCase);

            var marker = isThunderbolt ? " *** Thunderbolt/USB ***" : "";

            Console.WriteLine($"  [{ni.OperationalStatus}] {ni.Name}{marker}");
            Console.WriteLine($"    描述: {ni.Description}");
            Console.WriteLine($"    类型: {ni.NetworkInterfaceType}");
            Console.WriteLine($"    速度: {ni.Speed / 1_000_000} Mbps");

            if (ipv4 != null)
                Console.WriteLine($"    IPv4: {ipv4.Address}");

            Console.WriteLine();
        }

        Console.WriteLine("提示: Thunderbolt 连接后，Mac 和 Windows 会自动创建网络接口。");
        Console.WriteLine("      在 Mac 上检查: 系统设置 → 网络 → Thunderbolt Bridge");
        Console.WriteLine("      在 Windows 上检查: 设置 → 网络和 Internet → 以太网适配器");
    }

    enum TestMode : byte
    {
        ThroughputUpload = 1,
        ThroughputDownload = 2,
        Latency = 3,
    }
}
