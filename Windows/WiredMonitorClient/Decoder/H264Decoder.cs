using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using WiredMonitorClient.Diagnostics;

namespace WiredMonitorClient.Decoder;

public unsafe class H264Decoder : IDisposable
{
    private const int SwsFastBilinear = 1;

    private readonly ILogger _logger;
    private readonly AVCodecContext_get_format _getFormatCallback;
    private AVCodecContext* _codecContext;
    private AVBufferRef* _hwDeviceContext;
    private AVFrame* _frame;
    private AVFrame* _hwTransferFrame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private int _convBufferSize;
    private int _swsWidth;
    private int _swsHeight;
    private AVPixelFormat _swsSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVHWDeviceType _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    private int _width;
    private int _height;
    private bool _initialized;
    private bool _hardwareSetupFailed;
    private bool _hardwareFrameLogged;
    private bool _unexpectedSoftwareFrameLogged;
    private DateTime _lastDecodedHashTime = DateTime.MinValue;
    private ulong _lastDecodedHash;
    private readonly bool _decodedHashDiagnostics = Environment.GetEnvironmentVariable("WIRED_MONITOR_DIAG_HASH") == "1";
    private DateTime _lastDecodeWorkReportTime = DateTime.UtcNow;
    private long _decodeWorkTicks;
    private long _hwTransferTicks;
    private long _swsScaleTicks;
    private int _decodeWorkFrames;
    private int _skippedOutputFrames;

    public event EventHandler<DecodedFrame>? OnFrameDecoded;
    public Func<bool>? ShouldOutputFrame { get; set; }

    public H264Decoder(ILogger<H264Decoder> logger)
    {
        _logger = logger;
        _getFormatCallback = SelectHardwarePixelFormat;
    }

    public bool Initialize(int width, int height)
    {
        _width = width;
        _height = height;

        var ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath == null)
        {
            _logger.LogError("找不到 FFmpeg native 库目录");
            DiagLog.Write("FFmpeg 初始化失败: 找不到 native 库目录");
            return false;
        }

        ffmpeg.RootPath = ffmpegPath;
        DiagLog.Write($"FFmpeg RootPath: {ffmpegPath}");

        AVCodec* codec;
        try
        {
            DiagLog.Write($"FFmpeg version: {ffmpeg.av_version_info()}");
            codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查找 H.264 解码器异常");
            DiagLog.Write(ex, "查找 H.264 解码器异常");
            return false;
        }

        if (codec == null)
        {
            _logger.LogError("找不到 H.264 解码器");
            DiagLog.Write("FFmpeg 初始化失败: 找不到 H.264 解码器");
            return false;
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
        {
            _logger.LogError("无法分配解码器上下文");
            DiagLog.Write("FFmpeg 初始化失败: 无法分配解码器上下文");
            return false;
        }

        ConfigureCodecContext(width, height);
        if (!ConfigureRequiredHardwareDecoder(codec))
        {
            Dispose();
            return false;
        }

        var openResult = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (openResult < 0)
        {
            var error = ErrorToString(openResult);
            _logger.LogError("硬件 H.264 解码器打开失败: {Error}", error);
            DiagLog.Write($"FFmpeg 初始化失败: 硬件 H.264 解码器打开失败: {error}");
            Dispose();
            return false;
        }

        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_frame == null || _packet == null)
        {
            _logger.LogError("无法分配 H.264 解码帧/包");
            DiagLog.Write("FFmpeg 初始化失败: 无法分配 H.264 解码帧/包");
            Dispose();
            return false;
        }

        _initialized = true;
        _logger.LogInformation(
            "H.264 硬件解码器已初始化 ({Width}x{Height}, {DeviceType}, pix_fmt={PixelFormat})",
            width,
            height,
            _hwDeviceType,
            _hwPixelFormat);
        DiagLog.Write($"H.264 硬件解码器已初始化: {width}x{height}, {_hwDeviceType}, pix_fmt={_hwPixelFormat}");
        return true;
    }

    private void ConfigureCodecContext(int width, int height)
    {
        _codecContext->width = width;
        _codecContext->height = height;
        _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        _codecContext->thread_count = 1;
        _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecContext->hwaccel_flags =
            ffmpeg.AV_HWACCEL_FLAG_IGNORE_LEVEL |
            ffmpeg.AV_HWACCEL_FLAG_ALLOW_PROFILE_MISMATCH;
    }

    private bool ConfigureRequiredHardwareDecoder(AVCodec* codec)
    {
        foreach (var deviceType in GetRequestedHardwareDevices())
        {
            if (!TryFindHardwareConfig(codec, deviceType, out var pixelFormat))
            {
                DiagLog.Write($"硬件解码不可用: {deviceType} 没有匹配的 H.264 hw config");
                continue;
            }

            AVBufferRef* deviceContext = null;
            var createResult = ffmpeg.av_hwdevice_ctx_create(&deviceContext, deviceType, null, null, 0);
            if (createResult < 0 || deviceContext == null)
            {
                DiagLog.Write($"硬件解码设备创建失败: {deviceType}, {ErrorToString(createResult)}");
                continue;
            }

            var codecDeviceContext = ffmpeg.av_buffer_ref(deviceContext);
            if (codecDeviceContext == null)
            {
                ffmpeg.av_buffer_unref(&deviceContext);
                DiagLog.Write($"硬件解码设备引用失败: {deviceType}");
                continue;
            }

            _hwDeviceContext = deviceContext;
            _hwDeviceType = deviceType;
            _hwPixelFormat = pixelFormat;
            _codecContext->hw_device_ctx = codecDeviceContext;
            _codecContext->get_format = _getFormatCallback;

            _logger.LogInformation("H.264 硬件解码已配置: {DeviceType}, pix_fmt={PixelFormat}", deviceType, pixelFormat);
            DiagLog.Write($"H.264 硬件解码已配置: {deviceType}, pix_fmt={pixelFormat}");
            return true;
        }

        _logger.LogError("没有可用的 H.264 硬件解码器，已拒绝软件解码回退");
        DiagLog.Write("FFmpeg 初始化失败: 没有可用的 H.264 硬件解码器，已拒绝软件解码回退");
        return false;
    }

    private static AVHWDeviceType[] GetRequestedHardwareDevices()
    {
        var requested = Environment.GetEnvironmentVariable("WIRED_MONITOR_HWDEC");

        if (requested?.Equals("dxva2", StringComparison.OrdinalIgnoreCase) == true)
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 };

        if (requested?.Equals("d3d11", StringComparison.OrdinalIgnoreCase) == true
            || requested?.Equals("d3d11va", StringComparison.OrdinalIgnoreCase) == true)
            return new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA };

        return new[]
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
        };
    }

    private static bool TryFindHardwareConfig(AVCodec* codec, AVHWDeviceType deviceType, out AVPixelFormat pixelFormat)
    {
        pixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

        for (var i = 0;; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config == null)
                return false;

            var supportsDeviceContext =
                (config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0;
            if (supportsDeviceContext && config->device_type == deviceType)
            {
                pixelFormat = config->pix_fmt;
                return true;
            }
        }
    }

    private AVPixelFormat SelectHardwarePixelFormat(AVCodecContext* context, AVPixelFormat* pixelFormats)
    {
        var offeredFormats = new List<AVPixelFormat>();
        for (var current = pixelFormats; current != null && *current != AVPixelFormat.AV_PIX_FMT_NONE; current++)
        {
            offeredFormats.Add(*current);
            if (*current == _hwPixelFormat)
                return *current;
        }

        _hardwareSetupFailed = true;
        var offered = offeredFormats.Count == 0 ? "<none>" : string.Join(", ", offeredFormats);
        _logger.LogError(
            "FFmpeg 未提供期望硬件像素格式 {PixelFormat}，已拒绝软件解码回退。offered={OfferedFormats}",
            _hwPixelFormat,
            offered);
        DiagLog.Write($"FFmpeg 未提供期望硬件像素格式 {_hwPixelFormat}，已拒绝软件解码回退。offered={offered}");
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static string? ResolveFfmpegPath()
    {
        var direct = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (Directory.Exists(direct))
            return direct;

        var binRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        if (!Directory.Exists(binRoot))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(binRoot, "ffmpeg", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, "avcodec-62.dll")))
                return dir;
        }

        return null;
    }

    public void Decode(ReadOnlyMemory<byte> nalData, bool isKeyFrame)
    {
        if (!_initialized) return;
        if (nalData.IsEmpty) return;

        try
        {
            using var handle = nalData.Pin();
            var pData = (byte*)handle.Pointer;
            {
                _packet->data = pData;
                _packet->size = nalData.Length;
                _packet->flags = isKeyFrame ? ffmpeg.AV_PKT_FLAG_KEY : 0;

                var ret = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                if (_hardwareSetupFailed)
                    throw new InvalidOperationException($"硬件解码 setup 失败: {_hwDeviceType}/{_hwPixelFormat}");

                if (ret < 0)
                {
                    _logger.LogDebug("发送数据包失败: {Error}", ErrorToString(ret));
                    return;
                }

                while (ffmpeg.avcodec_receive_frame(_codecContext, _frame) == 0)
                {
                    if (ShouldOutputFrame?.Invoke() == false)
                    {
                        _skippedOutputFrames++;
                        ffmpeg.av_frame_unref(_frame);
                        continue;
                    }

                    var decodeWorkStart = Stopwatch.GetTimestamp();
                    var bgraFrame = ConvertFrameToBGRA(_frame);
                    if (bgraFrame != null)
                    {
                        RecordDecodeWork(decodeWorkStart);

                        if (_decodedHashDiagnostics)
                        {
                            var now = DateTime.UtcNow;
                            if ((now - _lastDecodedHashTime).TotalSeconds >= 1.0)
                            {
                                var hash = SampleHash(bgraFrame.Value.Buffer, _frame->width, _frame->height, _frame->width * 4);
                                DiagLog.Write($"硬解输出帧: {_frame->width}x{_frame->height}, bytes={bgraFrame.Value.Length}, hash={hash:x}, changed={hash != _lastDecodedHash}");
                                _lastDecodedHash = hash;
                                _lastDecodedHashTime = now;
                            }
                        }

                        var handler = OnFrameDecoded;
                        if (handler == null)
                        {
                            bgraFrame.Value.ReturnBuffer(bgraFrame.Value.Buffer);
                        }
                        else
                        {
                            handler.Invoke(this, new DecodedFrame
                            {
                                Width = _frame->width,
                                Height = _frame->height,
                                PixelData = bgraFrame.Value.Buffer,
                                PixelDataLength = bgraFrame.Value.Length,
                                ReturnBuffer = bgraFrame.Value.ReturnBuffer,
                            });
                        }
                    }

                    ffmpeg.av_frame_unref(_frame);
                }

                if (_hardwareSetupFailed)
                    throw new InvalidOperationException($"硬件解码 setup 失败: {_hwDeviceType}/{_hwPixelFormat}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "H.264 硬件解码异常");
            DiagLog.Write(ex, $"H.264 硬件解码异常, nalBytes={nalData.Length}, key={isKeyFrame}");
            throw;
        }
    }

    private BgraFrameBuffer? ConvertFrameToBGRA(AVFrame* frame)
    {
        if (frame->format != (int)_hwPixelFormat)
        {
            if (!_unexpectedSoftwareFrameLogged)
            {
                _unexpectedSoftwareFrameLogged = true;
                _logger.LogError("收到非硬件解码帧: {Actual}, expected={Expected}", (AVPixelFormat)frame->format, _hwPixelFormat);
                DiagLog.Write($"收到非硬件解码帧: {(AVPixelFormat)frame->format}, expected={_hwPixelFormat}");
            }

            return null;
        }

        var sourceFrame = TransferHardwareFrame(frame);
        if (sourceFrame == null)
            return null;

        try
        {
            if (!_hardwareFrameLogged)
            {
                _hardwareFrameLogged = true;
                _logger.LogInformation("H.264 硬件解码输出已启用: {DeviceType}", _hwDeviceType);
                DiagLog.Write($"H.264 硬件解码输出已启用: {_hwDeviceType}");
                DiagLog.Write($"D3D11VA硬解纹理: format={(AVPixelFormat)frame->format}, texture={PointerToHex(frame->data[0])}, slice={PointerToHex(frame->data[1])}, hwFramesCtx={PointerToHex(frame->hw_frames_ctx)}");
            }

            return ConvertSoftwareFrameToBGRA(sourceFrame);
        }
        finally
        {
            ffmpeg.av_frame_unref(_hwTransferFrame);
        }
    }

    private AVFrame* TransferHardwareFrame(AVFrame* frame)
    {
        if (_hwTransferFrame == null)
            _hwTransferFrame = ffmpeg.av_frame_alloc();

        if (_hwTransferFrame == null)
        {
            _logger.LogError("无法分配硬件解码传输帧");
            DiagLog.Write("硬件解码失败: 无法分配传输帧");
            return null;
        }

        ffmpeg.av_frame_unref(_hwTransferFrame);
        var transferStart = Stopwatch.GetTimestamp();
        var ret = ffmpeg.av_hwframe_transfer_data(_hwTransferFrame, frame, 0);
        var transferTicks = Stopwatch.GetTimestamp() - transferStart;
        if (ret < 0)
        {
            var error = ErrorToString(ret);
            _logger.LogWarning("硬件解码帧传输到系统内存失败: {Error}", error);
            DiagLog.Write($"硬件解码帧传输失败: {error}");
            return null;
        }

        _hwTransferTicks += transferTicks;

        if (_hwTransferFrame->width == 0)
            _hwTransferFrame->width = frame->width;
        if (_hwTransferFrame->height == 0)
            _hwTransferFrame->height = frame->height;

        return _hwTransferFrame;
    }

    private BgraFrameBuffer? ConvertSoftwareFrameToBGRA(AVFrame* frame)
    {
        int width = frame->width;
        int height = frame->height;
        var sourceFormat = (AVPixelFormat)frame->format;

        if (_swsContext == null || _swsWidth != width || _swsHeight != height || _swsSourceFormat != sourceFormat)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            _swsContext = ffmpeg.sws_getContext(
                width, height, sourceFormat,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                SwsFastBilinear, null, null, null);

            if (_swsContext == null)
            {
                _logger.LogError("无法创建像素格式转换上下文: {SourceFormat}", sourceFormat);
                DiagLog.Write($"无法创建像素格式转换上下文: {sourceFormat}");
                return null;
            }

            _convBufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, width, height, 1);
            _swsWidth = width;
            _swsHeight = height;
            _swsSourceFormat = sourceFormat;

            if (_convBufferSize <= 0)
            {
                _logger.LogError("无法分配 BGRA 转换缓冲区: {Size}", _convBufferSize);
                DiagLog.Write($"无法分配 BGRA 转换缓冲区: {_convBufferSize}");
                return null;
            }
        }

        var result = ArrayPool<byte>.Shared.Rent(_convBufferSize);
        try
        {
            fixed (byte* dst = result)
            {
                byte_ptrArray4 dstData = new();
                int_array4 dstLinesize = new();
                ffmpeg.av_image_fill_linesizes(ref dstLinesize, AVPixelFormat.AV_PIX_FMT_BGRA, width);
                dstData[0] = dst;
                dstData[1] = null;
                dstData[2] = null;
                dstData[3] = null;

                var swsStart = Stopwatch.GetTimestamp();
                var scaledRows = ffmpeg.sws_scale(_swsContext,
                    frame->data, frame->linesize, 0, height,
                    dstData, dstLinesize);
                var swsTicks = Stopwatch.GetTimestamp() - swsStart;

                if (scaledRows <= 0)
                {
                    _logger.LogWarning("硬解帧转换 BGRA 失败: {Rows}", scaledRows);
                    DiagLog.Write($"硬解帧转换 BGRA 失败: {scaledRows}");
                    ArrayPool<byte>.Shared.Return(result);
                    return null;
                }

                _swsScaleTicks += swsTicks;
            }

            return new BgraFrameBuffer(result, _convBufferSize, buffer => ArrayPool<byte>.Shared.Return(buffer));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(result);
            throw;
        }
    }

    private readonly record struct BgraFrameBuffer(byte[] Buffer, int Length, Action<byte[]> ReturnBuffer);

    private void RecordDecodeWork(long startTimestamp)
    {
        _decodeWorkTicks += Stopwatch.GetTimestamp() - startTimestamp;
        _decodeWorkFrames++;

        var now = DateTime.UtcNow;
        if ((now - _lastDecodeWorkReportTime).TotalSeconds < 2.0)
            return;

        var frames = Math.Max(1, _decodeWorkFrames);
        var avgMs = TicksToAverageMs(_decodeWorkTicks, frames);
        var transferAvgMs = TicksToAverageMs(_hwTransferTicks, frames);
        var swsAvgMs = TicksToAverageMs(_swsScaleTicks, frames);
        var otherAvgMs = Math.Max(0, avgMs - transferAvgMs - swsAvgMs);
        DiagLog.Write($"H264解码处理: frames={_decodeWorkFrames}, avg={avgMs:F1}ms, transfer={transferAvgMs:F1}ms, sws={swsAvgMs:F1}ms, other={otherAvgMs:F1}ms, skippedOutput={_skippedOutputFrames}");
        _decodeWorkTicks = 0;
        _hwTransferTicks = 0;
        _swsScaleTicks = 0;
        _decodeWorkFrames = 0;
        _skippedOutputFrames = 0;
        _lastDecodeWorkReportTime = now;
    }

    private static double TicksToAverageMs(long ticks, int frames) =>
        ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);

    private static string PointerToHex(void* pointer) => $"0x{(nuint)pointer:x}";

    private static string ErrorToString(int error)
    {
        const int bufferSize = 1024;
        var buffer = stackalloc byte[bufferSize];
        var result = ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
        if (result < 0)
            return error.ToString();

        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? error.ToString();
    }

    private static ulong SampleHash(byte[] data, int width, int height, int stride)
    {
        const ulong offsetBasis = 1469598103934665603;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        const int sampleRows = 8;
        const int sampleCols = 8;

        for (var row = 0; row < sampleRows; row++)
        {
            var y = Math.Min(height - 1, row * Math.Max(1, height / sampleRows));
            for (var col = 0; col < sampleCols; col++)
            {
                var x = Math.Min(width - 1, col * Math.Max(1, width / sampleCols));
                var offset = y * stride + x * 4;
                for (var i = 0; i < 4 && offset + i < data.Length; i++)
                {
                    hash ^= data[offset + i];
                    hash *= prime;
                }
            }
        }

        return hash;
    }

    public void Dispose()
    {
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }
        if (_hwTransferFrame != null)
        {
            AVFrame* p = _hwTransferFrame;
            ffmpeg.av_frame_free(&p);
            _hwTransferFrame = null;
        }
        if (_frame != null)
        {
            AVFrame* p = _frame;
            ffmpeg.av_frame_free(&p);
            _frame = null;
        }
        if (_packet != null)
        {
            AVPacket* p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }
        if (_codecContext != null)
        {
            AVCodecContext* p = _codecContext;
            ffmpeg.avcodec_free_context(&p);
            _codecContext = null;
        }
        if (_hwDeviceContext != null)
        {
            AVBufferRef* p = _hwDeviceContext;
            ffmpeg.av_buffer_unref(&p);
            _hwDeviceContext = null;
        }

        _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        _hwDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        _initialized = false;
    }
}

public readonly struct DecodedFrame
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] PixelData { get; init; }
    public int PixelDataLength { get; init; }
    public Action<byte[]>? ReturnBuffer { get; init; }
}
