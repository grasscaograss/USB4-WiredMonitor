namespace WiredMonitorClient.Protocol;

public enum PacketType : ushort
{
    Hello = 0x0001,
    HelloAck = 0x0002,
    DisplayInfo = 0x0010,
    FrameRequest = 0x0020,
    FrameH264 = 0x0030,
    FrameRaw = 0x0031,
    InputEvent = 0x0040,
    Stats = 0x0050,
    CursorPosition = 0x0060,
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
