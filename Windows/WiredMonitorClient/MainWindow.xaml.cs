using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows;
using Microsoft.Extensions.Logging;
using WiredMonitorClient.Decoder;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Network;
using WiredMonitorClient.Protocol;
using WiredMonitorClient.Rendering;

namespace WiredMonitorClient;

public partial class MainWindow : Window
{
    private const int MaxQueuedH264Frames = 2;

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
    private int _renderedFrameCount;

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
            await _receiver.ConnectAsync(host, port);
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
        if (WindowStyle == WindowStyle.None)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
            FullscreenButton.Content = "全屏";
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "退出全屏";
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
                _decodeCts?.Cancel();
                _decodeCts = new CancellationTokenSource();
                _decodeTask = Task.Run(() => DecodeLoop(_decodeCts.Token));
                _lastReceiveStatusUpdate = DateTime.MinValue;
                _lastReceiveError = null;
                StatusText.Visibility = Visibility.Visible;
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

        if (Volatile.Read(ref _queuedH264Frames) >= MaxQueuedH264Frames)
        {
            DiagLog.Write($"H264 解码队列积压，丢弃旧帧 queued={_queuedH264Frames}, currentKey={frame.IsKeyFrame}");
            ClearH264Queue();
            _resetDecoderOnNextKeyFrame = true;

            if (!frame.IsKeyFrame)
            {
                _dropUntilKeyFrame = true;
                return;
            }
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
                Interlocked.Decrement(ref _queuedH264Frames);
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
