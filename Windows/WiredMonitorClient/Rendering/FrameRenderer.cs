using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Protocol;

namespace WiredMonitorClient.Rendering;

public class FrameRenderer
{
    private readonly ILogger _logger;
    private readonly IntPtr _windowHandle;
    private WriteableBitmap? _bitmap;
    private D3DImageFramePresenter? _d3dPresenter;
    private int _width;
    private int _height;
    private int _stride;
    private readonly object _renderLock = new();
    private PendingFrame? _pendingFrame;
    private bool _renderScheduled;
    private DateTime _lastRenderReportTime = DateTime.UtcNow;
    private long _renderTicks;
    private long _d3dUploadTicks;
    private long _d3dUpdateSurfaceTicks;
    private long _d3dDirtyTicks;
    private int _renderFrames;
    private int _replacedPendingFrames;
    private string _rendererName = "none";

    public ImageSource? Bitmap => (ImageSource?)_d3dPresenter?.Image ?? _bitmap;

    public bool HasPendingFrame
    {
        get
        {
            lock (_renderLock)
            {
                return _pendingFrame != null;
            }
        }
    }

    public event EventHandler? OnFrameRendered;
    public event EventHandler? OnImageSourceChanged;

    public FrameRenderer(ILogger<FrameRenderer> logger, IntPtr windowHandle)
    {
        _logger = logger;
        _windowHandle = windowHandle;
    }

    public void Initialize(int width, int height)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        _d3dPresenter?.Dispose();
        _d3dPresenter = null;
        _bitmap = null;

        if (ShouldUseD3DImage())
        {
            var presenter = new D3DImageFramePresenter(_windowHandle);
            if (presenter.Initialize(width, height))
            {
                _d3dPresenter = presenter;
                _rendererName = "D3DImage";
                _logger.LogInformation("D3DImage 渲染器已初始化 ({Width}x{Height})", width, height);
                OnImageSourceChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            presenter.Dispose();
            _logger.LogWarning("D3DImage 渲染器初始化失败，回退到 WriteableBitmap");
        }

        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _rendererName = "WriteableBitmap";
        _logger.LogInformation("WriteableBitmap 渲染器已初始化 ({Width}x{Height})", width, height);
        OnImageSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenderRawFrame(RawFramePayload frame)
    {
        if (_bitmap == null && _d3dPresenter == null) return;

        var sourceStride = frame.BytesPerRow > 0 ? frame.BytesPerRow : _stride;
        var expectedSize = sourceStride * _height;
        if (!TryGetArray(frame.PixelData, out var data, out var offset)) return;
        if (data.Length - offset < expectedSize) return;

        QueueLatestFrame(new PendingFrame(data, offset, expectedSize, sourceStride, FlipVertical: true, ReturnBuffer: null));
    }

    public void RenderDecodedFrame(DecodedFrameData frame)
    {
        if (_bitmap == null && _d3dPresenter == null)
        {
            frame.ReturnBuffer?.Invoke(frame.PixelData);
            return;
        }

        var data = frame.PixelData;
        var length = frame.PixelDataLength > 0 ? frame.PixelDataLength : data.Length;
        if (data.Length == 0 || length <= 0)
        {
            frame.ReturnBuffer?.Invoke(data);
            return;
        }

        QueueLatestFrame(new PendingFrame(data, 0, length, _stride, FlipVertical: false, frame.ReturnBuffer));
    }

    private static bool TryGetArray(ReadOnlyMemory<byte> memory, out byte[] data, out int offset)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out var segment)
            && segment.Array != null)
        {
            data = segment.Array;
            offset = segment.Offset;
            return true;
        }

        data = memory.ToArray();
        offset = 0;
        return true;
    }

    private void QueueLatestFrame(PendingFrame frame)
    {
        var shouldSchedule = false;
        PendingFrame? replacedFrame = null;

        lock (_renderLock)
        {
            replacedFrame = _pendingFrame;
            if (replacedFrame != null)
                _replacedPendingFrames++;

            _pendingFrame = frame;
            if (!_renderScheduled)
            {
                _renderScheduled = true;
                shouldSchedule = true;
            }
        }

        replacedFrame?.ReturnBuffer?.Invoke(replacedFrame.Data);

        if (shouldSchedule)
            Application.Current.Dispatcher.BeginInvoke(RenderPendingFrame, DispatcherPriority.Render);
    }

    private void RenderPendingFrame()
    {
        PendingFrame? frame;
        lock (_renderLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        if (frame != null)
        {
            try
            {
                WriteFrame(frame);
            }
            finally
            {
                frame.ReturnBuffer?.Invoke(frame.Data);
            }
        }

        var shouldScheduleAgain = false;
        lock (_renderLock)
        {
            if (_pendingFrame != null)
            {
                shouldScheduleAgain = true;
            }
            else
            {
                _renderScheduled = false;
            }
        }

        if (shouldScheduleAgain)
            Application.Current.Dispatcher.BeginInvoke(RenderPendingFrame, DispatcherPriority.Render);
    }

    private void WriteFrame(PendingFrame frame)
    {
        if (_bitmap == null && _d3dPresenter == null) return;

        var copyLen = Math.Min(frame.Length, Math.Min(frame.Data.Length - frame.Offset, _stride * _height));
        if (copyLen <= 0) return;

        var renderStart = Stopwatch.GetTimestamp();
        if (_d3dPresenter != null)
        {
            if (_d3dPresenter.Present(frame.Data, frame.Offset, copyLen, frame.SourceStride, frame.FlipVertical, out var presentMetrics))
            {
                OnFrameRendered?.Invoke(this, EventArgs.Empty);
                RecordRenderWork(renderStart, presentMetrics);
                return;
            }

            _logger.LogWarning("D3DImage Present 失败，回退到 WriteableBitmap");
            DiagLog.Write("D3DImage Present 失败，回退到 WriteableBitmap");
            _d3dPresenter.Dispose();
            _d3dPresenter = null;
            _bitmap = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
            _rendererName = "WriteableBitmap";
            OnImageSourceChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_bitmap == null) return;

        _bitmap.Lock();
        try
        {
            unsafe
            {
                var dst = (byte*)_bitmap.BackBuffer;
                var dstStride = _bitmap.BackBufferStride;
                fixed (byte* srcBase = frame.Data)
                {
                    var src = srcBase + frame.Offset;
                    if (frame.FlipVertical)
                    {
                        for (int y = 0; y < _height; y++)
                        {
                            Buffer.MemoryCopy(
                                src + y * frame.SourceStride,
                                dst + (_height - 1 - y) * dstStride,
                                _stride,
                                _stride);
                        }
                    }
                    else
                    {
                        Buffer.MemoryCopy(src, dst, copyLen, copyLen);
                    }
                }
                _bitmap.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
            }
        }
        finally
        {
            _bitmap.Unlock();
        }

        OnFrameRendered?.Invoke(this, EventArgs.Empty);
        RecordRenderWork(renderStart);
    }

    private void RecordRenderWork(long startTimestamp, D3DImagePresentMetrics presentMetrics = default)
    {
        _renderTicks += Stopwatch.GetTimestamp() - startTimestamp;
        _d3dUploadTicks += presentMetrics.UploadTicks;
        _d3dUpdateSurfaceTicks += presentMetrics.UpdateSurfaceTicks;
        _d3dDirtyTicks += presentMetrics.DirtyTicks;
        _renderFrames++;

        var now = DateTime.UtcNow;
        if ((now - _lastRenderReportTime).TotalSeconds < 2.0)
            return;

        var frameCount = Math.Max(1, _renderFrames);
        var avgMs = TicksToAverageMs(_renderTicks, frameCount);
        var uploadMs = TicksToAverageMs(_d3dUploadTicks, frameCount);
        var updateMs = TicksToAverageMs(_d3dUpdateSurfaceTicks, frameCount);
        var dirtyMs = TicksToAverageMs(_d3dDirtyTicks, frameCount);
        var otherMs = Math.Max(0, avgMs - uploadMs - updateMs - dirtyMs);
        var frames = _renderFrames;
        var replaced = _replacedPendingFrames;
        var renderer = _rendererName;
        _renderTicks = 0;
        _d3dUploadTicks = 0;
        _d3dUpdateSurfaceTicks = 0;
        _d3dDirtyTicks = 0;
        _renderFrames = 0;
        _replacedPendingFrames = 0;
        _lastRenderReportTime = now;

        if (renderer == "D3DImage")
        {
            DiagLog.Write($"显示渲染处理: renderer={renderer}, frames={frames}, avg={avgMs:F1}ms, upload={uploadMs:F1}ms, updateSurface={updateMs:F1}ms, dirty={dirtyMs:F1}ms, other={otherMs:F1}ms, replaced={replaced}");
            return;
        }

        DiagLog.Write($"显示渲染处理: renderer={renderer}, frames={frames}, avg={avgMs:F1}ms, replaced={replaced}");
    }

    private static double TicksToAverageMs(long ticks, int frames) =>
        ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, frames);

    private static bool ShouldUseD3DImage()
    {
        var renderer = Environment.GetEnvironmentVariable("WIRED_MONITOR_RENDERER");
        return !string.Equals(renderer, "wpf", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(renderer, "writeablebitmap", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _d3dPresenter?.Dispose();
        _d3dPresenter = null;
        _bitmap = null;
        _rendererName = "none";
    }

    private sealed record PendingFrame(byte[] Data, int Offset, int Length, int SourceStride, bool FlipVertical, Action<byte[]>? ReturnBuffer);
}

public readonly struct DecodedFrameData
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] PixelData { get; init; }
    public int PixelDataLength { get; init; }
    public Action<byte[]>? ReturnBuffer { get; init; }
}
