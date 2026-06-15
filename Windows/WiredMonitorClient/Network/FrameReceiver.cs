using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using WiredMonitorClient.Display;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Protocol;

namespace WiredMonitorClient.Network;

public class FrameReceiver
{
    private const uint MaxPayloadLength = 128 * 1024 * 1024;
    private const int SocketBufferSize = 4 * 1024 * 1024;
    private const int LargePacketLogBytes = 256 * 1024;
    private const double SlowPacketReadMs = 20.0;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private long _connectionGeneration;

    public event EventHandler<FramePayload>? OnH264Frame;
    public event EventHandler<FramePayload>? OnHevcFrame;
    public event EventHandler<RawFramePayload>? OnRawFrame;
    public event EventHandler<CursorPositionPayload>? OnCursorPosition;
    public event EventHandler<WindowsControlModePayload>? OnWindowsControlMode;
    public event EventHandler<WindowsInputEventPayload>? OnWindowsInputEvent;
    public event EventHandler<bool>? OnConnectionChanged;
    public event EventHandler<string>? OnReceiveError;

    public bool IsConnected => _client?.Connected ?? false;

    public async Task ConnectAsync(string host, int port, ClientDisplayInfo displayInfo, CancellationToken ct = default)
    {
        DiagLog.Write($"ConnectAsync 开始: {host}:{port}");
        DisconnectCurrent(notify: false);
        var generation = Interlocked.Increment(ref _connectionGeneration);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var address = IPAddress.TryParse(host, out var parsedAddress) ? parsedAddress : null;
        var client = address == null
            ? new TcpClient()
            : new TcpClient(address.AddressFamily);
        client.NoDelay = true;
        client.ReceiveBufferSize = SocketBufferSize;
        client.SendBufferSize = 256 * 1024;

        var connectTask = address == null
            ? client.ConnectAsync(host, port)
            : client.ConnectAsync(address, port);

        try
        {
            await connectTask.WaitAsync(ConnectTimeout, ct);

            var stream = client.GetStream();
            _client = client;
            _stream = stream;
            _cts = cts;

            await SendHelloAsync(stream, displayInfo, cts.Token);
            DiagLog.Write($"TCP 连接成功: recvBuf={client.ReceiveBufferSize}, sendBuf={client.SendBufferSize}, generation={generation}");

            if (!IsCurrentConnection(generation))
                throw new OperationCanceledException("连接已被新的会话替换");

            OnConnectionChanged?.Invoke(this, true);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ReceiveLoop(stream, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    if (IsCurrentConnection(generation))
                    {
                        Console.WriteLine($"[网络] 断开: {ex.Message}");
                        DiagLog.Write(ex, "ReceiveLoop 异常");
                        OnReceiveError?.Invoke(this, ex.Message);
                    }
                }
                finally
                {
                    if (IsCurrentConnection(generation))
                    {
                        ClearConnectionFields();
                        OnConnectionChanged?.Invoke(this, false);
                    }
                }
            }, CancellationToken.None);
        }
        catch
        {
            if (IsCurrentConnection(generation))
                DisconnectCurrent(notify: false);

            CloseClient(client, cts);

            throw;
        }
    }

    public void Disconnect()
    {
        DisconnectCurrent(notify: true);
    }

    private async Task ReceiveLoop(NetworkStream stream, CancellationToken ct)
    {
        var headerBuf = new byte[ProtocolConstants.HeaderSize];
        byte[]? dataBuf = null;
        var frameCount = 0;
        var lastFpsTime = DateTime.UtcNow;
        var fpsCount = 0;
        var cursorCount = 0;
        var loggedFirstPacket = false;
        var loggedUnknownPacketTypes = new HashSet<ushort>();
        var lastPacketReadLogTime = DateTime.MinValue;

        while (!ct.IsCancellationRequested && _stream != null)
        {
            await ReadExact(stream, headerBuf, 0, headerBuf.Length, ct);

            PacketType packetType;
            int payloadLength;

            if (PacketHeader.TryDecode(headerBuf, out var header))
            {
                if (header.PayloadLength > MaxPayloadLength)
                    throw new InvalidDataException($"无效 payload 长度: {header.PayloadLength}");

                packetType = header.Type;
                payloadLength = (int)header.PayloadLength;
                dataBuf = dataBuf?.Length >= payloadLength ? dataBuf : new byte[payloadLength];
                var payloadReadStart = Stopwatch.GetTimestamp();
                await ReadExact(stream, dataBuf, 0, payloadLength, ct);
                var payloadReadMs = ElapsedMilliseconds(payloadReadStart);

                if (!loggedFirstPacket)
                {
                    DiagLog.Write($"首包: protocol=WM type={packetType} payloadLen={payloadLength}");
                    loggedFirstPacket = true;
                }

                var packetReadLogNow = DateTime.UtcNow;
                if ((packetType == PacketType.FrameH264 || packetType == PacketType.FrameHevc) &&
                    (payloadLength >= LargePacketLogBytes || payloadReadMs >= SlowPacketReadMs) &&
                    (packetReadLogNow - lastPacketReadLogTime).TotalMilliseconds >= 500)
                {
                    DiagLog.Write($"网络读包: payload={payloadLength}, read={payloadReadMs:F1}ms");
                    lastPacketReadLogTime = packetReadLogNow;
                }
            }
            else
            {
                if (loggedFirstPacket)
                    throw new InvalidDataException($"WM 协议流失步: {Convert.ToHexString(headerBuf)}");

                var legacyFrame = await TryReadLegacyFrame(stream, headerBuf, dataBuf, ct);
                if (legacyFrame == null)
                    throw new InvalidDataException($"无效包头: {Convert.ToHexString(headerBuf)}");

                dataBuf = legacyFrame.Value.Buffer;
                packetType = legacyFrame.Value.PacketType;
                payloadLength = legacyFrame.Value.PayloadLength;

                if (!loggedFirstPacket)
                {
                    DiagLog.Write($"首包: protocol=legacy type={packetType} payloadLen={payloadLength} first10={Convert.ToHexString(headerBuf)}");
                    loggedFirstPacket = true;
                }
            }

            var isVideoFrame = false;
            switch (packetType)
            {
                case PacketType.FrameH264:
                    OnH264Frame?.Invoke(this, FramePayload.Parse(dataBuf, payloadLength));
                    isVideoFrame = true;
                    break;
                case PacketType.FrameHevc:
                    OnHevcFrame?.Invoke(this, FramePayload.Parse(dataBuf, payloadLength));
                    isVideoFrame = true;
                    break;
                case PacketType.FrameRaw:
                    OnRawFrame?.Invoke(this, RawFramePayload.Parse(dataBuf, payloadLength, 0, 0));
                    isVideoFrame = true;
                    break;
                case PacketType.CursorPosition:
                    cursorCount++;
                    OnCursorPosition?.Invoke(this, CursorPositionPayload.Parse(dataBuf, payloadLength));
                    break;
                case PacketType.WindowsControlMode:
                    OnWindowsControlMode?.Invoke(this, WindowsControlModePayload.Parse(dataBuf, payloadLength));
                    break;
                case PacketType.WindowsInputEvent:
                    OnWindowsInputEvent?.Invoke(this, WindowsInputEventPayload.Parse(dataBuf, payloadLength));
                    break;
                default:
                    var rawType = (ushort)packetType;
                    if (loggedUnknownPacketTypes.Add(rawType))
                        DiagLog.Write($"忽略未知包类型: 0x{rawType:X4}, payloadLen={payloadLength}");
                    break;
            }

            if (isVideoFrame)
            {
                frameCount++;
                fpsCount++;
            }

            var now = DateTime.UtcNow;
            if ((now - lastFpsTime).TotalSeconds >= 2.0)
            {
                var fps = fpsCount / (now - lastFpsTime).TotalSeconds;
                var cursorFps = cursorCount / (now - lastFpsTime).TotalSeconds;
                Console.WriteLine($"[网络] FPS: {fps:F1}, Cursor: {cursorFps:F1}");
                DiagLog.Write($"网络 FPS: {fps:F1}, cursorFPS={cursorFps:F1}, totalFrames={frameCount}");
                fpsCount = 0;
                cursorCount = 0;
                lastFpsTime = now;
            }
        }
    }

    private async Task SendHelloAsync(NetworkStream stream, ClientDisplayInfo displayInfo, CancellationToken ct)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)displayInfo.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)displayInfo.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), (uint)displayInfo.RefreshRate);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), (uint)displayInfo.Dpi);

        var header = new PacketHeader
        {
            Magic = ProtocolConstants.Magic,
            Version = ProtocolConstants.Version,
            Type = PacketType.Hello,
            PayloadLength = (uint)payload.Length,
        }.Encode();

        var packet = new byte[header.Length + payload.Length];
        Buffer.BlockCopy(header, 0, packet, 0, header.Length);
        Buffer.BlockCopy(payload, 0, packet, header.Length, payload.Length);

        await stream.WriteAsync(packet.AsMemory(0, packet.Length), ct);
        await stream.FlushAsync(ct);
        DiagLog.Write($"发送HELLO: {displayInfo.Width}x{displayInfo.Height} @ {displayInfo.RefreshRate}Hz, dpi={displayInfo.Dpi}");
    }

    private async Task<LegacyFrame?> TryReadLegacyFrame(NetworkStream stream, byte[] firstBytes, byte[]? dataBuf, CancellationToken ct)
    {
        var packetLength = BitConverter.ToUInt32(firstBytes, 0);
        if (packetLength < 2 || packetLength > MaxPayloadLength + 2)
            return null;

        var packetType = firstBytes[4] switch
        {
            0 => PacketType.FrameH264,
            1 => PacketType.FrameRaw,
            _ => default
        };
        if (packetType == default)
            return null;

        var payloadLength = (int)packetLength - 2;
        dataBuf = dataBuf?.Length >= payloadLength ? dataBuf : new byte[payloadLength];

        var bytesAlreadyRead = Math.Min(payloadLength, firstBytes.Length - 6);
        if (bytesAlreadyRead > 0)
            Buffer.BlockCopy(firstBytes, 6, dataBuf, 0, bytesAlreadyRead);

        var remaining = payloadLength - bytesAlreadyRead;
        if (remaining > 0)
            await ReadExact(stream, dataBuf, bytesAlreadyRead, remaining, ct);

        return new LegacyFrame(packetType, payloadLength, dataBuf);
    }

    private readonly record struct LegacyFrame(PacketType PacketType, int PayloadLength, byte[] Buffer);

    private async Task ReadExact(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
    {
        var end = offset + count;
        while (offset < end)
        {
            var n = await stream.ReadAsync(buf.AsMemory(offset, end - offset), ct);
            if (n == 0) throw new IOException("连接已关闭");
            offset += n;
        }
    }

    private bool IsCurrentConnection(long generation)
    {
        return Volatile.Read(ref _connectionGeneration) == generation;
    }

    private void DisconnectCurrent(bool notify)
    {
        Interlocked.Increment(ref _connectionGeneration);
        var cts = _cts;
        var stream = _stream;
        var client = _client;
        ClearConnectionFields();
        CloseClient(client, cts, stream);

        if (notify)
            OnConnectionChanged?.Invoke(this, false);
    }

    private void ClearConnectionFields()
    {
        _cts = null;
        _stream = null;
        _client = null;
    }

    private static void CloseClient(TcpClient? client, CancellationTokenSource? cts, NetworkStream? stream = null)
    {
        try { cts?.Cancel(); } catch { }
        try { stream?.Close(); } catch { }
        try { client?.Close(); } catch { }
        cts?.Dispose();
    }

    private static double ElapsedMilliseconds(long startTimestamp)
    {
        return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }
}
