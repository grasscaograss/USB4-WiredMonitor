using System.IO;
using System.Net;
using System.Net.Sockets;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Protocol;

namespace WiredMonitorClient.Network;

public class FrameReceiver
{
    private const uint MaxPayloadLength = 128 * 1024 * 1024;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;

    public event EventHandler<FramePayload>? OnH264Frame;
    public event EventHandler<RawFramePayload>? OnRawFrame;
    public event EventHandler<bool>? OnConnectionChanged;
    public event EventHandler<string>? OnReceiveError;

    public bool IsConnected => _client?.Connected ?? false;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        DiagLog.Write($"ConnectAsync 开始: {host}:{port}");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var address = IPAddress.TryParse(host, out var parsedAddress) ? parsedAddress : null;
        _client = address == null
            ? new TcpClient()
            : new TcpClient(address.AddressFamily);
        _client.NoDelay = true;
        _client.ReceiveBufferSize = 256 * 1024;

        var connectTask = address == null
            ? _client.ConnectAsync(host, port)
            : _client.ConnectAsync(address, port);
        await connectTask.WaitAsync(TimeSpan.FromSeconds(5), ct);

        _stream = _client.GetStream();
        DiagLog.Write("TCP 连接成功");

        OnConnectionChanged?.Invoke(this, true);

        _ = Task.Run(async () =>
        {
            try { await ReceiveLoop(_cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[网络] 断开: {ex.Message}");
                DiagLog.Write(ex, "ReceiveLoop 异常");
                OnReceiveError?.Invoke(this, ex.Message);
            }
            OnConnectionChanged?.Invoke(this, false);
        }, _cts.Token);
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _stream?.Close();
        _client?.Close();
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var headerBuf = new byte[ProtocolConstants.HeaderSize];
        byte[]? dataBuf = null;
        var frameCount = 0;
        var lastFpsTime = DateTime.UtcNow;
        var fpsCount = 0;
        var loggedFirstPacket = false;

        while (!ct.IsCancellationRequested && _stream != null)
        {
            await ReadExact(headerBuf, 0, headerBuf.Length, ct);

            PacketType packetType;
            int payloadLength;

            if (PacketHeader.TryDecode(headerBuf, out var header))
            {
                if (header.PayloadLength == 0 || header.PayloadLength > MaxPayloadLength)
                    throw new InvalidDataException($"无效 payload 长度: {header.PayloadLength}");

                packetType = header.Type;
                payloadLength = (int)header.PayloadLength;
                dataBuf = dataBuf?.Length >= payloadLength ? dataBuf : new byte[payloadLength];
                await ReadExact(dataBuf, 0, payloadLength, ct);

                if (!loggedFirstPacket)
                {
                    DiagLog.Write($"首包: protocol=WM type={packetType} payloadLen={payloadLength}");
                    loggedFirstPacket = true;
                }
            }
            else
            {
                var legacyFrame = await TryReadLegacyFrame(headerBuf, dataBuf, ct);
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

            switch (packetType)
            {
                case PacketType.FrameH264:
                    OnH264Frame?.Invoke(this, FramePayload.Parse(dataBuf, payloadLength));
                    break;
                case PacketType.FrameRaw:
                    OnRawFrame?.Invoke(this, RawFramePayload.Parse(dataBuf, payloadLength, 0, 0));
                    break;
            }

            frameCount++;
            fpsCount++;
            var now = DateTime.UtcNow;
            if ((now - lastFpsTime).TotalSeconds >= 2.0)
            {
                var fps = fpsCount / (now - lastFpsTime).TotalSeconds;
                Console.WriteLine($"[网络] FPS: {fps:F1}");
                DiagLog.Write($"网络 FPS: {fps:F1}, totalFrames={frameCount}");
                fpsCount = 0;
                lastFpsTime = now;
            }
        }
    }

    private async Task<LegacyFrame?> TryReadLegacyFrame(byte[] firstBytes, byte[]? dataBuf, CancellationToken ct)
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
            await ReadExact(dataBuf, bytesAlreadyRead, remaining, ct);

        return new LegacyFrame(packetType, payloadLength, dataBuf);
    }

    private readonly record struct LegacyFrame(PacketType PacketType, int PayloadLength, byte[] Buffer);

    private async Task ReadExact(byte[] buf, int offset, int count, CancellationToken ct)
    {
        var end = offset + count;
        while (offset < end)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(offset, end - offset), ct);
            if (n == 0) throw new IOException("连接已关闭");
            offset += n;
        }
    }
}
