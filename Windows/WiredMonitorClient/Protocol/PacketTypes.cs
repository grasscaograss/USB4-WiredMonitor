namespace WiredMonitorClient.Protocol;

public enum PacketType : ushort
{
    Hello = 0x0001,
    HelloAck = 0x0002,
    DisplayInfo = 0x0010,
    FrameRequest = 0x0020,
    FrameH264 = 0x0030,
    FrameRaw = 0x0031,
    FrameHevc = 0x0032,
    InputEvent = 0x0040,
    Stats = 0x0050,
    CursorPosition = 0x0060,
    WindowsControlMode = 0x0070,
    WindowsInputEvent = 0x0071,
}

public static class ProtocolConstants
{
    public const ushort Magic = 0x574D; // "WM"
    public const ushort Version = 0x0001;
    public const int ControlPort = 9801;
    public const int VideoPort = 9802;
    public const int HeaderSize = 10;
    public const int H264FrameMetadataSize = 17;
    public const int H264FrameMetadataWithSize = 25;
    public const int RawFrameMetadataSize = 16;
    public const int RawFrameMetadataWithSize = 28;
    public const int CursorPositionPayloadSize = 17;
    public const int WindowsControlModePayloadSize = 1;
}

public enum WindowsInputEventType : byte
{
    MouseMove = 0x01,
    MouseDown = 0x02,
    MouseUp = 0x03,
    KeyDown = 0x04,
    KeyUp = 0x05,
    Scroll = 0x06,
}

public readonly struct PacketHeader
{
    public ushort Magic { get; init; }
    public ushort Version { get; init; }
    public PacketType Type { get; init; }
    public uint PayloadLength { get; init; }

    public byte[] Encode()
    {
        var data = new byte[ProtocolConstants.HeaderSize];
        BitConverter.GetBytes((ushort)Magic).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)Version).CopyTo(data, 2);
        BitConverter.GetBytes((ushort)Type).CopyTo(data, 4);
        BitConverter.GetBytes((uint)PayloadLength).CopyTo(data, 6);
        return data;
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out PacketHeader header)
    {
        header = default;
        if (data.Length < ProtocolConstants.HeaderSize) return false;

        var magic = BitConverter.ToUInt16(data[..2]);
        if (magic != ProtocolConstants.Magic) return false;

        var version = BitConverter.ToUInt16(data[2..4]);
        if (version != ProtocolConstants.Version) return false;

        header = new PacketHeader
        {
            Magic = magic,
            Version = version,
            Type = (PacketType)BitConverter.ToUInt16(data[4..6]),
            PayloadLength = BitConverter.ToUInt32(data[6..10]),
        };
        return true;
    }
}

public readonly struct FramePayload
{
    public ulong FrameIndex { get; init; }
    public ulong Timestamp { get; init; }
    public bool IsKeyFrame { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public ReadOnlyMemory<byte> Data { get; init; }

    public static FramePayload Parse(byte[] payload, int payloadLength)
    {
        return Parse(payload.AsSpan(0, payloadLength));
    }

    public static FramePayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtocolConstants.H264FrameMetadataSize)
            throw new ArgumentException("H.264 frame payload is too short.", nameof(payload));

        var dataOffset = ProtocolConstants.H264FrameMetadataSize;
        var width = 0;
        var height = 0;

        if (payload.Length >= ProtocolConstants.H264FrameMetadataWithSize)
        {
            var payloadWidth = (int)BitConverter.ToUInt32(payload[17..21]);
            var payloadHeight = (int)BitConverter.ToUInt32(payload[21..25]);
            if (payloadWidth >= 16 && payloadWidth <= 16384 &&
                payloadHeight >= 16 && payloadHeight <= 16384)
            {
                width = payloadWidth;
                height = payloadHeight;
                dataOffset = ProtocolConstants.H264FrameMetadataWithSize;
            }
        }

        return new FramePayload
        {
            FrameIndex = BitConverter.ToUInt64(payload[..8]),
            Timestamp = BitConverter.ToUInt64(payload[8..16]),
            IsKeyFrame = payload[16] != 0,
            Width = width,
            Height = height,
            Data = payload[dataOffset..].ToArray(),
        };
    }
}

public readonly struct RawFramePayload
{
    public ulong FrameIndex { get; init; }
    public ulong Timestamp { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BytesPerRow { get; init; }
    public ReadOnlyMemory<byte> PixelData { get; init; }

    public static RawFramePayload Parse(byte[] payload, int payloadLength, int width, int height)
    {
        return Parse(payload.AsSpan(0, payloadLength), width, height);
    }

    public static RawFramePayload Parse(ReadOnlySpan<byte> payload, int width, int height)
    {
        if (payload.Length < ProtocolConstants.RawFrameMetadataSize)
            throw new ArgumentException("RAW frame payload is too short.", nameof(payload));

        if (payload.Length >= ProtocolConstants.RawFrameMetadataWithSize)
        {
            var payloadWidth = (int)BitConverter.ToUInt32(payload[16..20]);
            var payloadHeight = (int)BitConverter.ToUInt32(payload[20..24]);
            var bytesPerRow = (int)BitConverter.ToUInt32(payload[24..28]);

            if (payloadWidth > 0 &&
                payloadHeight > 0 &&
                bytesPerRow >= payloadWidth * 4 &&
                payload.Length == ProtocolConstants.RawFrameMetadataWithSize + bytesPerRow * payloadHeight)
            {
                return new RawFramePayload
                {
                    FrameIndex = BitConverter.ToUInt64(payload[..8]),
                    Timestamp = BitConverter.ToUInt64(payload[8..16]),
                    Width = payloadWidth,
                    Height = payloadHeight,
                    BytesPerRow = bytesPerRow,
                    PixelData = payload[ProtocolConstants.RawFrameMetadataWithSize..].ToArray(),
                };
            }
        }

        return new RawFramePayload
        {
            FrameIndex = BitConverter.ToUInt64(payload[..8]),
            Timestamp = BitConverter.ToUInt64(payload[8..16]),
            Width = width,
            Height = height,
            BytesPerRow = width > 0 ? width * 4 : 0,
            PixelData = payload[16..].ToArray(),
        };
    }
}

public readonly struct CursorPositionPayload
{
    public ulong Timestamp { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public bool Visible { get; init; }

    public static CursorPositionPayload Parse(byte[] payload, int payloadLength)
    {
        return Parse(payload.AsSpan(0, payloadLength));
    }

    public static CursorPositionPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtocolConstants.CursorPositionPayloadSize)
            throw new ArgumentException("Cursor position payload is too short.", nameof(payload));

        return new CursorPositionPayload
        {
            Timestamp = BitConverter.ToUInt64(payload[..8]),
            X = (int)BitConverter.ToUInt32(payload[8..12]),
            Y = (int)BitConverter.ToUInt32(payload[12..16]),
            Visible = payload[16] != 0,
        };
    }
}

public readonly struct WindowsControlModePayload
{
    public bool Enabled { get; init; }

    public static WindowsControlModePayload Parse(byte[] payload, int payloadLength)
    {
        return Parse(payload.AsSpan(0, payloadLength));
    }

    public static WindowsControlModePayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ProtocolConstants.WindowsControlModePayloadSize)
            throw new ArgumentException("Windows control mode payload is too short.", nameof(payload));

        return new WindowsControlModePayload
        {
            Enabled = payload[0] != 0,
        };
    }
}

public readonly struct WindowsInputEventPayload
{
    public WindowsInputEventType EventType { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public byte Button { get; init; }
    public ushort VirtualKey { get; init; }
    public int DeltaX { get; init; }
    public int DeltaY { get; init; }

    public static WindowsInputEventPayload Parse(byte[] payload, int payloadLength)
    {
        return Parse(payload.AsSpan(0, payloadLength));
    }

    public static WindowsInputEventPayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            throw new ArgumentException("Windows input payload is too short.", nameof(payload));

        var eventType = (WindowsInputEventType)payload[0];
        return eventType switch
        {
            WindowsInputEventType.MouseMove => ParseMouseMove(payload),
            WindowsInputEventType.MouseDown or WindowsInputEventType.MouseUp => ParseMouseButton(eventType, payload),
            WindowsInputEventType.KeyDown or WindowsInputEventType.KeyUp => ParseKey(eventType, payload),
            WindowsInputEventType.Scroll => ParseScroll(payload),
            _ => throw new ArgumentException($"Unknown Windows input event type: 0x{payload[0]:X2}", nameof(payload)),
        };
    }

    private static WindowsInputEventPayload ParseMouseMove(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
            throw new ArgumentException("Mouse move payload is too short.", nameof(payload));

        return new WindowsInputEventPayload
        {
            EventType = WindowsInputEventType.MouseMove,
            X = BitConverter.ToInt32(payload[1..5]),
            Y = BitConverter.ToInt32(payload[5..9]),
        };
    }

    private static WindowsInputEventPayload ParseMouseButton(WindowsInputEventType eventType, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 10)
            throw new ArgumentException("Mouse button payload is too short.", nameof(payload));

        return new WindowsInputEventPayload
        {
            EventType = eventType,
            Button = payload[1],
            X = BitConverter.ToInt32(payload[2..6]),
            Y = BitConverter.ToInt32(payload[6..10]),
        };
    }

    private static WindowsInputEventPayload ParseKey(WindowsInputEventType eventType, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
            throw new ArgumentException("Keyboard payload is too short.", nameof(payload));

        return new WindowsInputEventPayload
        {
            EventType = eventType,
            VirtualKey = BitConverter.ToUInt16(payload[1..3]),
        };
    }

    private static WindowsInputEventPayload ParseScroll(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
            throw new ArgumentException("Scroll payload is too short.", nameof(payload));

        return new WindowsInputEventPayload
        {
            EventType = WindowsInputEventType.Scroll,
            DeltaX = BitConverter.ToInt32(payload[1..5]),
            DeltaY = BitConverter.ToInt32(payload[5..9]),
        };
    }
}
