using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using WiredMonitorClient.Decoder;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Display;
using WiredMonitorClient.Network;
using WiredMonitorClient.Protocol;
using WiredMonitorClient.Rendering;

namespace WiredMonitorClient;

public partial class MainWindow : Window
{
    private const int ReceiveBackpressureHighWatermark = 1;
    private const int ReceiveBackpressureLowWatermark = 0;
    private const int MaxReceiveBackpressureSleepMs = 40;
    private const int EmergencyQueuedH264Frames = 90;
    private const int MaxQueueLatencyMs = 800;
    private const long MacAbsoluteEpochOffsetMs = 978_307_200_000;

    private readonly ILogger _logger;
    private readonly FrameReceiver _receiver;
    private readonly FrameRenderer _renderer;
    private readonly ILoggerFactory _loggerFactory;
    private H264Decoder? _h264Decoder;
    private readonly ConcurrentQueue<FramePayload> _h264Queue = new();
    private readonly SemaphoreSlim _h264Signal = new(0);
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private int _queuedH264Frames;
    private volatile bool _dropUntilKeyFrame;
    private volatile bool _resetDecoderOnNextKeyFrame;
    private DateTime _lastCatchUpTime = DateTime.MinValue;

    private int _frameCount;
    private long _totalBytes;
    private DateTime _lastFpsUpdate = DateTime.UtcNow;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _displayWidth;
    private int _displayHeight;
    private bool _decoderFailed;
    private bool _hasDecodedFrame;
    private DateTime _lastReceiveStatusUpdate = DateTime.MinValue;
    private string? _lastReceiveError;
    private long _minObservedMacClockOffsetMs = long.MaxValue;
    private DateTime _lastLatencyLogTime = DateTime.MinValue;
    private DateTime _lastBackpressureLogTime = DateTime.MinValue;
    private int _renderedFrameCount;
    private bool _isFullscreen;
    private WindowState _previousWindowState = WindowState.Normal;
    private WindowStyle _previousWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResizeMode = ResizeMode.CanResize;

    public MainWindow()
    {
        InitializeComponent();
        DiagLog.Write($"应用启动: {AppContext.BaseDirectory}");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        _logger = _loggerFactory.CreateLogger<MainWindow>();
        _receiver = new FrameReceiver();
        _renderer = new FrameRenderer(_loggerFactory.CreateLogger<FrameRenderer>());

        _receiver.OnH264Frame += OnH264Frame;
        _receiver.OnRawFrame += OnRawFrame;
        _receiver.OnCursorPosition += OnCursorPosition;
        _receiver.OnConnectionChanged += OnConnectionChanged;
        _receiver.OnReceiveError += OnReceiveError;

        _renderer.OnFrameRendered += OnFrameRendered;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var host = HostTextBox.Text.Trim();
        var port = int.Parse(PortTextBox.Text.Trim());

        Console.WriteLine($"[UI] 连接按钮点击: {host}:{port}");
        DiagLog.Write($"UI 请求连接: {host}:{port}");
        ConnectButton.IsEnabled = false;
        StatusText.Text = $"正在连接 {host}:{port}...";

        try
        {
            var displayInfo = WindowsDisplayInfo.FromWindow(this);
            DiagLog.Write($"连接使用显示器信息: {displayInfo.Width}x{displayInfo.Height}@{displayInfo.RefreshRate}, dpi={displayInfo.Dpi}");
            await _receiver.ConnectAsync(host, port, displayInfo);
            Console.WriteLine("[UI] ConnectAsync 返回成功");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UI] 连接异常: {ex}");
            DiagLog.Write(ex, "连接异常");
            MessageBox.Show($"连接失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            ConnectButton.IsEnabled = true;
            StatusText.Text = "连接失败";
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _receiver.Disconnect();
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            TopToolbar.Visibility = Visibility.Visible;
            BottomStatusBar.Visibility = Visibility.Visible;
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            WindowState = _previousWindowState;
            FullscreenButton.Content = "全屏";
            _isFullscreen = false;
        }
        else
        {
            _previousWindowState = WindowState;
            _previousWindowStyle = WindowStyle;
            _previousResizeMode = ResizeMode;
            TopToolbar.Visibility = Visibility.Collapsed;
            BottomStatusBar.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "退出全屏";
            _isFullscreen = true;
        }
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        Console.WriteLine($"[UI] OnConnectionChanged: {connected}");
        DiagLog.Write($"连接状态: {connected}");
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
                ConnectionStatus.Text = "已连接";
                StatusText.Text = "等待画面数据...";
                StatusText.Foreground = System.Windows.Media.Brushes.White;
                ConnectButton.IsEnabled = false;
                DisconnectButton.IsEnabled = true;
                _frameCount = 0;
                _totalBytes = 0;
                _displayWidth = 0;
                _displayHeight = 0;
                _decoderFailed = false;
                _hasDecodedFrame = false;
                _renderedFrameCount = 0;
                ClearH264Queue();
                _dropUntilKeyFrame = false;
                _resetDecoderOnNextKeyFrame = false;
                _lastCatchUpTime = DateTime.MinValue;
                _minObservedMacClockOffsetMs = long.MaxValue;
                _lastLatencyLogTime = DateTime.MinValue;
                _lastBackpressureLogTime = DateTime.MinValue;
                _decodeCts?.Cancel();
                _decodeCts = new CancellationTokenSource();
                _decodeTask = Task.Run(() => DecodeLoop(_decodeCts.Token));
                _lastReceiveStatusUpdate = DateTime.MinValue;
                _lastReceiveError = null;
                StatusText.Visibility = Visibility.Visible;
                RemoteCursor.Visibility = Visibility.Collapsed;
            }
            else
            {
                StatusDot.Fill = System.Windows.Media.Brushes.Red;
                ConnectionStatus.Text = "未连接";
                StatusText.Text = _lastReceiveError == null
                    ? "未连接 - 请输入 Mac 的 Thunderbolt IP 地址"
                    : $"接收中断: {_lastReceiveError}";
                StatusText.Visibility = Visibility.Visible;
                DisplayImage.Source = null;
                RemoteCursor.Visibility = Visibility.Collapsed;
                ConnectButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                FpsText.Text = "";
                FrameSizeText.Text = "";
                _decodeCts?.Cancel();
                _h264Signal.Release();
                ClearH264Queue();
                _h264Decoder?.Dispose();
                _h264Decoder = null;
            }
        });
    }

    private void OnH264Frame(object? sender, FramePayload frame)
    {
        _frameCount++;
        _totalBytes += frame.Data.Length;
        if (_frameCount <= 3 || frame.IsKeyFrame)
            DiagLog.Write($"H264帧: idx={frame.FrameIndex}, key={frame.IsKeyFrame}, size={frame.Width}x{frame.Height}, bytes={frame.Data.Length}");

        var now = DateTime.UtcNow;
        if (!_hasDecodedFrame && (now - _lastReceiveStatusUpdate).TotalMilliseconds >= 500)
        {
            _lastReceiveStatusUpdate = now;
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = frame.IsKeyFrame
                    ? $"收到 H.264 关键帧 #{frame.FrameIndex}，正在解码..."
                    : $"收到 H.264 帧 #{frame.FrameIndex}，等待解码输出...";
                StatusText.Foreground = System.Windows.Media.Brushes.White;
            });
        }

        // 解码器初始化失败时只统计帧，不崩溃
        if (_decoderFailed)
        {
            if (_frameCount % 100 == 0)
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"接收中 (无解码器) FPS: {_frameCount} 累计: {_totalBytes / 1024}KB";
                });
            }
            return;
        }

        var queuedFrames = Volatile.Read(ref _queuedH264Frames);
        var queueLatencyMs = EstimateQueueLatencyMs(frame);
        if ((now - _lastLatencyLogTime).TotalSeconds >= 2.0)
        {
            _lastLatencyLogTime = now;
            DiagLog.Write($"H264低延迟状态: queue={queuedFrames}, lag={queueLatencyMs}ms, key={frame.IsKeyFrame}, idx={frame.FrameIndex}");
        }

        if (queuedFrames >= EmergencyQueuedH264Frames)
        {
            DiagLog.Write($"H264 解码落后，清空到下一关键帧 queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}");
            ClearH264Queue();
            _resetDecoderOnNextKeyFrame = true;

            if (!frame.IsKeyFrame)
            {
                _dropUntilKeyFrame = true;
                return;
            }
        }
        else if (queueLatencyMs > MaxQueueLatencyMs && (now - _lastCatchUpTime).TotalSeconds >= 2.0)
        {
            DiagLog.Write($"H264 延迟过高，快速追帧到下一关键帧 queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            _lastCatchUpTime = now;
            ClearH264Queue();
            _resetDecoderOnNextKeyFrame = true;

            if (!frame.IsKeyFrame)
            {
                _dropUntilKeyFrame = true;
                return;
            }
        }
        else if (queueLatencyMs > MaxQueueLatencyMs && (now - _lastLatencyLogTime).TotalSeconds >= 0.5)
        {
            DiagLog.Write($"H264延迟偏高但等待追帧冷却: queue={queuedFrames}, lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
        }

        if (_dropUntilKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                return;
            }

            DiagLog.Write($"收到关键帧，恢复低延迟解码 idx={frame.FrameIndex}");
            ClearH264Queue();
            _dropUntilKeyFrame = false;
            _resetDecoderOnNextKeyFrame = true;
        }

        _h264Queue.Enqueue(frame);
        Interlocked.Increment(ref _queuedH264Frames);
        _h264Signal.Release();
        ApplyReceiveBackpressureIfNeeded();
    }

    private void ApplyReceiveBackpressureIfNeeded()
    {
        if (Volatile.Read(ref _queuedH264Frames) < ReceiveBackpressureHighWatermark)
            return;

        var sleepMs = 0;
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _queuedH264Frames) > ReceiveBackpressureLowWatermark &&
               sw.ElapsedMilliseconds < MaxReceiveBackpressureSleepMs)
        {
            Thread.Sleep(1);
            sleepMs++;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastBackpressureLogTime).TotalSeconds >= 2.0)
        {
            _lastBackpressureLogTime = now;
            DiagLog.Write($"接收背压: slept={sleepMs}ms, queue={Volatile.Read(ref _queuedH264Frames)}");
        }
    }

    private long EstimateQueueLatencyMs(FramePayload frame)
    {
        if (frame.Timestamp == 0)
            return 0;

        var localMacEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - MacAbsoluteEpochOffsetMs;
        var observedOffset = localMacEpochMs - unchecked((long)frame.Timestamp);
        if (observedOffset < _minObservedMacClockOffsetMs)
            _minObservedMacClockOffsetMs = observedOffset;

        return Math.Max(0, observedOffset - _minObservedMacClockOffsetMs);
    }

    private async Task DecodeLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _h264Signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (_h264Queue.TryDequeue(out var frame))
            {
                if (Interlocked.Decrement(ref _queuedH264Frames) < 0)
                    Interlocked.Exchange(ref _queuedH264Frames, 0);

                DecodeH264Frame(frame);
            }
        }
    }

    private void DecodeH264Frame(FramePayload frame)
    {
        if (_resetDecoderOnNextKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                return;
            }

            _h264Decoder?.Dispose();
            _h264Decoder = null;
            _decoderFailed = false;
            _resetDecoderOnNextKeyFrame = false;
            DiagLog.Write($"重置解码器并从关键帧恢复 idx={frame.FrameIndex}");
        }

        if (_h264Decoder == null)
        {
            _h264Decoder = new H264Decoder(_loggerFactory.CreateLogger<H264Decoder>());
            var decoderWidth = frame.Width > 0 ? frame.Width : 1920;
            var decoderHeight = frame.Height > 0 ? frame.Height : 1080;

            try
            {
                if (_h264Decoder.Initialize(decoderWidth, decoderHeight))
                {
                    _h264Decoder.OnFrameDecoded += OnDecodedFrame;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = $"H.264 硬解中 {decoderWidth}x{decoderHeight}...";
                    });
                }
                else
                {
                    _h264Decoder.Dispose();
                    _h264Decoder = null;
                    _decoderFailed = true;
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = "硬件解码器初始化失败 - 需要支持 D3D11VA/DXVA2 的 GPU 和 FFmpeg 库";
                        StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[解码器] 初始化异常: {ex.Message}");
                DiagLog.Write(ex, "解码器初始化异常");
                _h264Decoder?.Dispose();
                _h264Decoder = null;
                _decoderFailed = true;
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"解码器不可用: {ex.Message}";
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                });
            }
        }

        if (_h264Decoder != null)
        {
            try
            {
                _h264Decoder.Decode(frame.Data, frame.IsKeyFrame);
            }
            catch (Exception ex)
            {
                DiagLog.Write(ex, "H.264 解码失败，等待下一关键帧");
                _decoderFailed = false;
                _resetDecoderOnNextKeyFrame = true;
                _dropUntilKeyFrame = true;
            }
        }
    }

    private void ClearH264Queue()
    {
        while (_h264Queue.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _queuedH264Frames, 0);
    }

    private void OnDecodedFrame(object? sender, DecodedFrame decodedFrame)
    {
        _hasDecodedFrame = true;

        // 如果分辨率变化，重新初始化
        if (decodedFrame.Width != _displayWidth || decodedFrame.Height != _displayHeight)
        {
            _displayWidth = decodedFrame.Width;
            _displayHeight = decodedFrame.Height;

            Dispatcher.Invoke(() =>
            {
                _renderer.Initialize(_displayWidth, _displayHeight);
                DisplayImage.Source = _renderer.Bitmap;
                StatusText.Text = $"H.264 {_displayWidth}x{_displayHeight}";
                StatusText.Visibility = Visibility.Collapsed;
            });
        }

        _renderer.RenderDecodedFrame(new DecodedFrameData
        {
            Width = decodedFrame.Width,
            Height = decodedFrame.Height,
            PixelData = decodedFrame.PixelData,
            PixelDataLength = decodedFrame.PixelDataLength,
            ReturnBuffer = decodedFrame.ReturnBuffer,
        });
    }

    private void OnRawFrame(object? sender, RawFramePayload frame)
    {
        _frameCount++;
        _totalBytes += frame.PixelData.Length;
        if (_frameCount <= 3)
            DiagLog.Write($"RAW帧: idx={frame.FrameIndex}, bytes={frame.PixelData.Length}");

        var pixelData = frame.PixelData.Span;
        var pixelCount = pixelData.Length / 4;

        if (_displayWidth == 0)
        {
            if (frame.Width > 0 && frame.Height > 0)
            {
                _displayWidth = frame.Width;
                _displayHeight = frame.Height;
            }

            var resolutions = new (int w, int h)[]
            {
                (1920, 1080), (2560, 1440), (3840, 2160),
                (1440, 900), (1680, 1050), (1280, 800),
                (2560, 1600), (1728, 1117), (1470, 956),
                (1512, 982), (1800, 1169)
            };

            foreach (var (w, h) in resolutions)
            {
                if (_displayWidth == 0 && pixelData.Length >= w * h * 4)
                {
                    _displayWidth = w;
                    _displayHeight = h;
                    break;
                }
            }

            if (_displayWidth == 0)
            {
                _displayWidth = (int)Math.Sqrt(pixelCount * 16 / 9.0);
                _displayWidth = (_displayWidth / 2) * 2;
                _displayHeight = pixelCount / _displayWidth;
            }

            Dispatcher.Invoke(() =>
            {
                _renderer.Initialize(_displayWidth, _displayHeight);
                DisplayImage.Source = _renderer.Bitmap;
                StatusText.Text = $"RAW {_displayWidth}x{_displayHeight}";
                StatusText.Visibility = Visibility.Collapsed;
            });

            _logger.LogInformation("检测到 RAW 帧分辨率: {Width}x{Height}", _displayWidth, _displayHeight);
            DiagLog.Write($"检测到 RAW 帧分辨率: {_displayWidth}x{_displayHeight}");
        }

        _renderer.RenderRawFrame(frame);
    }

    private void OnCursorPosition(object? sender, CursorPositionPayload cursor)
    {
        Dispatcher.Invoke(() => UpdateRemoteCursor(cursor));
    }

    private void UpdateRemoteCursor(CursorPositionPayload cursor)
    {
        if (!cursor.Visible || _displayWidth <= 0 || _displayHeight <= 0)
        {
            RemoteCursor.Visibility = Visibility.Collapsed;
            return;
        }

        var hostWidth = DisplayHost.ActualWidth;
        var hostHeight = DisplayHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            RemoteCursor.Visibility = Visibility.Collapsed;
            return;
        }

        var scale = Math.Min(hostWidth / _displayWidth, hostHeight / _displayHeight);
        var renderedWidth = _displayWidth * scale;
        var renderedHeight = _displayHeight * scale;
        var offsetX = (hostWidth - renderedWidth) / 2.0;
        var offsetY = (hostHeight - renderedHeight) / 2.0;

        Canvas.SetLeft(RemoteCursor, offsetX + cursor.X * scale);
        Canvas.SetTop(RemoteCursor, offsetY + cursor.Y * scale);
        RemoteCursor.Visibility = Visibility.Visible;
    }

    private void OnReceiveError(object? sender, string message)
    {
        _lastReceiveError = message;
        DiagLog.Write($"接收错误: {message}");
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"接收中断: {message}";
            StatusText.Visibility = Visibility.Visible;
            StatusText.Foreground = System.Windows.Media.Brushes.Orange;
        });
    }

    private void OnFrameRendered(object? sender, EventArgs e)
    {
        _renderedFrameCount++;
        if (_renderedFrameCount <= 3)
        {
            DiagLog.Write($"渲染帧 #{_renderedFrameCount}");
        }

        var now = DateTime.UtcNow;
        if ((now - _lastFpsUpdate).TotalSeconds >= 0.5)
        {
            var fps = _frameCount / (now - _lastFpsUpdate).TotalSeconds;
            var elapsed = _stopwatch.Elapsed;
            var avgMbps = (_totalBytes * 8.0) / elapsed.TotalSeconds / 1_000_000.0;
            _frameCount = 0;
            _lastFpsUpdate = now;

            Dispatcher.Invoke(() =>
            {
                FpsText.Text = $"FPS: {fps:F0}";
                FrameSizeText.Text = $"带宽: {avgMbps:F1} Mbps";
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _decodeCts?.Cancel();
        _h264Signal.Release();
        _receiver.Disconnect();
        _h264Decoder?.Dispose();
        _loggerFactory.Dispose();
        base.OnClosed(e);
    }
}
