using System.Diagnostics;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WiredMonitorClient.Decoder;
using WiredMonitorClient.Diagnostics;
using WiredMonitorClient.Display;
using WiredMonitorClient.Network;
using WiredMonitorClient.Protocol;
using WiredMonitorClient.Rendering;
using WiredMonitorClient.Video;

namespace WiredMonitorClient;

public partial class MainWindow : Window
{
    private readonly record struct QueuedH264Frame(FramePayload Payload, VideoDecoderCodec Codec, bool SuppressOutput);

    private const int ReceiveBackpressureHighWatermark = 6;
    private const int ReceiveBackpressureLowWatermark = 2;
    private static readonly int MaxReceiveBackpressureSleepMs = 0;
    private const int EmergencyQueuedH264Frames = 12;
    private const int MaxQueueLatencyMs = 120;
    private const int ResumeQueueLatencyMs = 70;
    private const int MaxQueuedFramesForOutput = 1;
    private const long MacAbsoluteEpochOffsetMs = 978_307_200_000;
    private static readonly TimeSpan LocalCursorIdleTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly FrameReceiver _receiver;
    private readonly FrameRenderer _renderer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IntPtr _windowHandle;
    private readonly DispatcherTimer _localCursorIdleTimer;
    private H264Decoder? _h264Decoder;
    private readonly ConcurrentQueue<QueuedH264Frame> _h264Queue = new();
    private readonly SemaphoreSlim _h264Signal = new(0);
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private int _queuedH264Frames;
    private volatile bool _dropUntilKeyFrame;
    private volatile bool _resetDecoderOnNextKeyFrame;
    private volatile bool _suppressOutputUntilLowLatency;
    private bool _suppressCurrentDecodeOutput;
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
    private int _initialH264FrameLogs;
    private int _initialRawFrameLogs;
    private int _renderedFrameCount;
    private bool _isFullscreen;
    private WindowState _previousWindowState = WindowState.Normal;
    private WindowStyle _previousWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResizeMode = ResizeMode.CanResize;
    private VideoDecoderCodec _decoderCodec = VideoDecoderCodec.H264;
    private bool _manualDisconnectRequested;
    private string? _lastHost;
    private int _lastPort;
    private ClientDisplayInfo? _lastDisplayInfo;
    private CancellationTokenSource? _autoReconnectCts;
    private int _autoReconnectAttempt;
    private int _lastLayoutPhysicalWidth;
    private int _lastLayoutPhysicalHeight;
    private double _lastLayoutScale;
    private bool _lastLayoutPixelPerfect;
    private DateTime _lastLayoutLogTime = DateTime.MinValue;
    private CancellationTokenSource? _usb4IpDetectionCts;
    private bool _isLocalCursorOverDisplay;
    private bool _isLocalCursorHidden;
    private Point? _lastLocalCursorPosition;

    public MainWindow()
    {
        InitializeComponent();
        _windowHandle = new WindowInteropHelper(this).EnsureHandle();
        DiagLog.Write($"应用启动: {AppContext.BaseDirectory}");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        _logger = _loggerFactory.CreateLogger<MainWindow>();
        _receiver = new FrameReceiver();
        _renderer = new FrameRenderer(_loggerFactory.CreateLogger<FrameRenderer>(), _windowHandle);

        _receiver.OnH264Frame += (_, frame) => OnCompressedFrame(frame, VideoDecoderCodec.H264);
        _receiver.OnHevcFrame += (_, frame) => OnCompressedFrame(frame, VideoDecoderCodec.Hevc);
        _receiver.OnRawFrame += OnRawFrame;
        _receiver.OnCursorPosition += OnCursorPosition;
        _receiver.OnConnectionChanged += OnConnectionChanged;
        _receiver.OnReceiveError += OnReceiveError;

        _renderer.OnFrameRendered += OnFrameRendered;
        _renderer.OnImageSourceChanged += OnImageSourceChanged;
        DisplayHost.SizeChanged += (_, _) => UpdateDisplayImageLayout();
        Loaded += (_, _) => StartUsb4IpAutoDetection();

        _localCursorIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LocalCursorIdleTimeout
        };
        _localCursorIdleTimer.Tick += (_, _) => HideLocalCursorIfIdle();
        DisplayHost.MouseEnter += OnDisplayHostMouseEnter;
        DisplayHost.MouseMove += OnDisplayHostMouseMove;
        DisplayHost.MouseLeave += OnDisplayHostMouseLeave;
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var host = HostTextBox.Text.Trim();
        var port = ParsePortOrDefault();

        Console.WriteLine($"[UI] 连接按钮点击: {host}:{port}");
        DiagLog.Write($"UI 请求连接: {host}:{port}");
        _manualDisconnectRequested = false;
        _autoReconnectCts?.Cancel();
        ConnectButton.IsEnabled = false;
        StatusText.Text = $"正在连接 {host}:{port}...";

        try
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                StatusText.Text = "正在检测 Mac USB4 IP...";
                var detected = await Usb4IpDetector.DetectAsync(port);
                if (detected == null)
                    throw new InvalidOperationException("没有检测到 Mac USB4 IP");

                ApplyDetectedUsb4Ip(detected);
                host = detected.Host;
            }

            var displayInfo = WindowsDisplayInfo.FromWindow(this);
            DiagLog.Write($"连接使用显示器信息: {displayInfo.Width}x{displayInfo.Height}@{displayInfo.RefreshRate}, dpi={displayInfo.Dpi}");
            await ConnectToServerAsync(host, port, displayInfo, isAutoReconnect: false);
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
        _manualDisconnectRequested = true;
        _autoReconnectCts?.Cancel();
        _receiver.Disconnect();
    }

    private async Task ConnectToServerAsync(string host, int port, ClientDisplayInfo displayInfo, bool isAutoReconnect)
    {
        _lastHost = host;
        _lastPort = port;
        _lastDisplayInfo = displayInfo;
        if (isAutoReconnect)
            DiagLog.Write($"自动重连开始: {host}:{port}, {displayInfo.Width}x{displayInfo.Height}@{displayInfo.RefreshRate}, dpi={displayInfo.Dpi}");

        await _receiver.ConnectAsync(host, port, displayInfo);
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

    private void OnDisplayHostMouseEnter(object sender, MouseEventArgs e)
    {
        _isLocalCursorOverDisplay = true;
        ShowLocalCursor();
        RestartLocalCursorIdleTimer(e.GetPosition(DisplayHost));
    }

    private void OnDisplayHostMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(DisplayHost);
        if (!_isLocalCursorHidden && _lastLocalCursorPosition is { } last &&
            Math.Abs(position.X - last.X) < 0.5 &&
            Math.Abs(position.Y - last.Y) < 0.5)
        {
            return;
        }

        _isLocalCursorOverDisplay = true;
        ShowLocalCursor();
        RestartLocalCursorIdleTimer(position);
    }

    private void OnDisplayHostMouseLeave(object sender, MouseEventArgs e)
    {
        _isLocalCursorOverDisplay = false;
        _lastLocalCursorPosition = null;
        _localCursorIdleTimer.Stop();
        ShowLocalCursor();
    }

    private void RestartLocalCursorIdleTimer(Point position)
    {
        _lastLocalCursorPosition = position;
        _localCursorIdleTimer.Stop();
        _localCursorIdleTimer.Start();
    }

    private void HideLocalCursorIfIdle()
    {
        _localCursorIdleTimer.Stop();
        if (!_isLocalCursorOverDisplay)
            return;

        DisplayHost.Cursor = Cursors.None;
        _isLocalCursorHidden = true;
    }

    private void ShowLocalCursor()
    {
        if (!_isLocalCursorHidden)
            return;

        DisplayHost.Cursor = null;
        _isLocalCursorHidden = false;
    }

    private void OnConnectionChanged(object? sender, bool connected)
    {
        Console.WriteLine($"[UI] OnConnectionChanged: {connected}");
        DiagLog.Write($"连接状态: {connected}");
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                _autoReconnectCts?.Cancel();
                _autoReconnectAttempt = 0;
                _manualDisconnectRequested = false;
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
                _suppressOutputUntilLowLatency = false;
                _suppressCurrentDecodeOutput = false;
                _lastCatchUpTime = DateTime.MinValue;
                _minObservedMacClockOffsetMs = long.MaxValue;
                _lastLatencyLogTime = DateTime.MinValue;
                _lastBackpressureLogTime = DateTime.MinValue;
                _initialH264FrameLogs = 0;
                _initialRawFrameLogs = 0;
                _decodeCts?.Cancel();
                _decodeCts = new CancellationTokenSource();
                _decodeTask = Task.Run(() => DecodeLoop(_decodeCts.Token));
                _decoderCodec = VideoDecoderCodec.H264;
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

        if (!connected && !_manualDisconnectRequested)
            ScheduleAutoReconnect();
    }

    private void ScheduleAutoReconnect()
    {
        if (_manualDisconnectRequested || string.IsNullOrWhiteSpace(_lastHost) || _lastPort <= 0)
            return;

        _autoReconnectCts?.Cancel();
        var reconnectCts = new CancellationTokenSource();
        _autoReconnectCts = reconnectCts;
        var token = reconnectCts.Token;
        var attempt = Math.Min(Interlocked.Increment(ref _autoReconnectAttempt), 10);
        var delayMs = Math.Min(5000, 500 * attempt);
        var host = _lastHost;
        var port = _lastPort;

        DiagLog.Write($"自动重连已安排: {delayMs}ms 后尝试 #{attempt}, {host}:{port}");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                if (token.IsCancellationRequested || _manualDisconnectRequested)
                    return;

                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = $"连接中断，正在自动重连 #{attempt}...";
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Foreground = System.Windows.Media.Brushes.White;
                    ConnectButton.IsEnabled = false;
                    DisconnectButton.IsEnabled = false;
                });

                var reconnectTarget = await Dispatcher.InvokeAsync(() =>
                {
                    var uiHost = HostTextBox.Text.Trim();
                    var uiPort = ParsePortOrDefault();
                    return (
                        Host: string.IsNullOrWhiteSpace(uiHost) ? host : uiHost,
                        Port: uiPort > 0 ? uiPort : port,
                        DisplayInfo: WindowsDisplayInfo.FromWindow(this));
                });
                await ConnectToServerAsync(
                    reconnectTarget.Host,
                    reconnectTarget.Port,
                    reconnectTarget.DisplayInfo,
                    isAutoReconnect: true);
                DiagLog.Write($"自动重连成功: attempt={attempt}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DiagLog.Write(ex, $"自动重连失败: attempt={attempt}");
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusText.Text = $"自动重连失败，继续重试: {ex.Message}";
                    StatusText.Visibility = Visibility.Visible;
                    StatusText.Foreground = System.Windows.Media.Brushes.Orange;
                    ConnectButton.IsEnabled = true;
                    DisconnectButton.IsEnabled = false;
                });

                if (!token.IsCancellationRequested && !_manualDisconnectRequested)
                    ScheduleAutoReconnect();
            }
        }, token);
    }

    private void StartUsb4IpAutoDetection()
    {
        _usb4IpDetectionCts?.Cancel();
        var cts = new CancellationTokenSource();
        _usb4IpDetectionCts = cts;
        _ = Task.Run(() => Usb4IpDetectionLoop(cts.Token), cts.Token);
    }

    private async Task Usb4IpDetectionLoop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);

            while (!ct.IsCancellationRequested)
            {
                if (_receiver.IsConnected)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Usb4IpStatusText.Text = $"USB4IP: {HostTextBox.Text.Trim()}";
                        Usb4IpStatusText.Foreground = Brushes.LimeGreen;
                    });
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    continue;
                }

                var target = await Dispatcher.InvokeAsync(() => (
                    CurrentHost: HostTextBox.Text.Trim(),
                    Port: ParsePortOrDefault()));

                await Dispatcher.InvokeAsync(() =>
                {
                    Usb4IpStatusText.Text = "USB4IP: 检测中";
                    Usb4IpStatusText.Foreground = Brushes.LightGray;
                });

                var result = await Usb4IpDetector.DetectAsync(target.Port, target.CurrentHost, ct);
                if (result != null)
                {
                    await Dispatcher.InvokeAsync(() => ApplyDetectedUsb4Ip(result));
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Usb4IpStatusText.Text = "USB4IP: 未找到";
                        Usb4IpStatusText.Foreground = Brushes.Orange;
                    });
                    await Task.Delay(TimeSpan.FromSeconds(8), ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagLog.Write(ex, "USB4IP 自动检测异常");
            await Dispatcher.InvokeAsync(() =>
            {
                Usb4IpStatusText.Text = "USB4IP: 检测异常";
                Usb4IpStatusText.Foreground = Brushes.Orange;
            });
        }
    }

    private void ApplyDetectedUsb4Ip(Usb4IpDetectionResult result)
    {
        _lastHost = result.Host;
        _lastPort = result.Port;

        Usb4IpStatusText.Text = $"USB4IP: {result.Host}";
        Usb4IpStatusText.Foreground = Brushes.LimeGreen;

        if (!_receiver.IsConnected &&
            !HostTextBox.IsKeyboardFocusWithin &&
            !string.Equals(HostTextBox.Text.Trim(), result.Host, StringComparison.OrdinalIgnoreCase))
        {
            HostTextBox.Text = result.Host;
            DiagLog.Write(
                $"USB4IP 自动更新界面地址: {result.Host}, local={result.LocalAddress}, iface={result.InterfaceName}");
        }

        if (!_receiver.IsConnected && StatusText.Visibility == Visibility.Visible)
        {
            StatusText.Text = $"检测到 Mac USB4 IP: {result.Host}";
            StatusText.Foreground = Brushes.White;
        }
    }

    private int ParsePortOrDefault()
    {
        return int.TryParse(PortTextBox.Text.Trim(), out var port) && port > 0 && port <= 65535
            ? port
            : ProtocolConstants.VideoPort;
    }

    private void OnCompressedFrame(FramePayload frame, VideoDecoderCodec codec)
    {
        _frameCount++;
        _totalBytes += frame.Data.Length;
        var codecName = codec == VideoDecoderCodec.Hevc ? "HEVC" : "H264";
        if (_initialH264FrameLogs < 3 || frame.IsKeyFrame)
        {
            if (!frame.IsKeyFrame)
                _initialH264FrameLogs++;
            DiagLog.Write($"{codecName}帧: idx={frame.FrameIndex}, key={frame.IsKeyFrame}, size={frame.Width}x{frame.Height}, bytes={frame.Data.Length}");
        }

        var now = DateTime.UtcNow;
        if (!_hasDecodedFrame && (now - _lastReceiveStatusUpdate).TotalMilliseconds >= 500)
        {
            _lastReceiveStatusUpdate = now;
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = frame.IsKeyFrame
                    ? $"收到 {codecName} 关键帧 #{frame.FrameIndex}，正在解码..."
                    : $"收到 {codecName} 帧 #{frame.FrameIndex}，等待解码输出...";
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

        var suppressOutput = false;

        if (_dropUntilKeyFrame)
        {
            if (!frame.IsKeyFrame)
            {
                return;
            }

            ClearH264Queue();
            _dropUntilKeyFrame = false;
            _resetDecoderOnNextKeyFrame = true;
            _suppressOutputUntilLowLatency = queueLatencyMs > ResumeQueueLatencyMs;
            suppressOutput = _suppressOutputUntilLowLatency;

            if (suppressOutput)
                DiagLog.Write($"收到追帧关键帧，先预热解码器 lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
            else
                DiagLog.Write($"收到低延迟关键帧，恢复解码 lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
        }
        else if (_suppressOutputUntilLowLatency)
        {
            if (queueLatencyMs <= ResumeQueueLatencyMs || queuedFrames <= MaxQueuedFramesForOutput)
            {
                _suppressOutputUntilLowLatency = false;
                DiagLog.Write($"追帧预热完成，恢复输出 queued={queuedFrames}, lag={queueLatencyMs}ms, key={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            }
            else
            {
                suppressOutput = true;
                if (queuedFrames >= EmergencyQueuedH264Frames)
                {
                    DiagLog.Write($"追帧期间队列仍落后，清空到下一关键帧 queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
                    ClearH264Queue();
                    _resetDecoderOnNextKeyFrame = true;

                    if (!frame.IsKeyFrame)
                    {
                        _dropUntilKeyFrame = true;
                        return;
                    }
                }
            }
        }
        else if (queuedFrames >= EmergencyQueuedH264Frames && queueLatencyMs > ResumeQueueLatencyMs)
        {
            DiagLog.Write($"H264 解码落后，清空到下一关键帧 queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            _lastCatchUpTime = now;
            ClearH264Queue();
            _resetDecoderOnNextKeyFrame = true;

            if (!frame.IsKeyFrame)
            {
                _dropUntilKeyFrame = true;
                _suppressOutputUntilLowLatency = true;
                return;
            }

            _suppressOutputUntilLowLatency = queueLatencyMs > ResumeQueueLatencyMs;
            suppressOutput = _suppressOutputUntilLowLatency;
            if (suppressOutput)
                DiagLog.Write($"从过期关键帧预热解码器 lag={queueLatencyMs}ms, idx={frame.FrameIndex}");
        }
        else if (queuedFrames > MaxQueuedFramesForOutput && queueLatencyMs > MaxQueueLatencyMs)
        {
            DiagLog.Write($"H264 延迟过高，软追帧跳过过期输出 queued={queuedFrames}, lag={queueLatencyMs}ms, currentKey={frame.IsKeyFrame}, idx={frame.FrameIndex}");
            _lastCatchUpTime = now;
            _suppressOutputUntilLowLatency = true;
            suppressOutput = true;
        }

        EnqueueH264Frame(frame, codec, suppressOutput);
        ApplyReceiveBackpressureIfNeeded();
    }

    private void EnqueueH264Frame(FramePayload frame, VideoDecoderCodec codec, bool suppressOutput)
    {
        _h264Queue.Enqueue(new QueuedH264Frame(frame, codec, suppressOutput));
        Interlocked.Increment(ref _queuedH264Frames);
        _h264Signal.Release();
    }

    private void ApplyReceiveBackpressureIfNeeded()
    {
        if (MaxReceiveBackpressureSleepMs <= 0)
            return;

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

            while (_h264Queue.TryDequeue(out var queuedFrame))
            {
                if (Interlocked.Decrement(ref _queuedH264Frames) < 0)
                    Interlocked.Exchange(ref _queuedH264Frames, 0);

                DecodeH264Frame(queuedFrame);
            }
        }
    }

    private void DecodeH264Frame(QueuedH264Frame queuedFrame)
    {
        var frame = queuedFrame.Payload;
        var codec = queuedFrame.Codec;
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

        if (_h264Decoder != null && _decoderCodec != codec)
        {
            if (!frame.IsKeyFrame)
                return;

            _h264Decoder.Dispose();
            _h264Decoder = null;
            _decoderFailed = false;
            DiagLog.Write($"切换视频解码器: {_decoderCodec} -> {codec}, idx={frame.FrameIndex}");
        }

        if (_h264Decoder == null)
        {
            _decoderCodec = codec;
            _h264Decoder = new H264Decoder(_loggerFactory.CreateLogger<H264Decoder>(), codec);
            var decoderWidth = frame.Width > 0 ? frame.Width : 1920;
            var decoderHeight = frame.Height > 0 ? frame.Height : 1080;

            try
            {
                if (_h264Decoder.Initialize(decoderWidth, decoderHeight))
                {
                    _h264Decoder.ShouldOutputFrame = ShouldOutputDecodedFrame;
                    _h264Decoder.ShouldUseD3D11Output = () => _renderer.CanUseD3D11Direct;
                    _h264Decoder.OnFrameDecoded += OnDecodedFrame;
                    _h264Decoder.OnD3D11FrameDecoded += OnD3D11FrameDecoded;
                    Dispatcher.Invoke(() =>
                    {
                        _displayWidth = decoderWidth;
                        _displayHeight = decoderHeight;
                        _renderer.Initialize(_displayWidth, _displayHeight);
                        DisplayImage.Source = _renderer.Bitmap;
                        UpdateDisplayImageLayout();
                        StatusText.Visibility = Visibility.Collapsed;
                    });
                }
                else
                {
                    _h264Decoder.Dispose();
                    _h264Decoder = null;
                    _decoderFailed = true;
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = $"{codec} 硬件解码器初始化失败 - 需要支持 D3D11VA/DXVA2 的 GPU 和 FFmpeg 库";
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
                _suppressCurrentDecodeOutput = queuedFrame.SuppressOutput;
                _h264Decoder.Decode(frame.Data, frame.IsKeyFrame);
            }
            catch (Exception ex)
            {
                DiagLog.Write(ex, "H.264 解码失败，等待下一关键帧");
                _decoderFailed = false;
                _resetDecoderOnNextKeyFrame = true;
                _dropUntilKeyFrame = true;
                _suppressOutputUntilLowLatency = true;
            }
            finally
            {
                _suppressCurrentDecodeOutput = false;
            }
        }
    }

    private bool ShouldOutputDecodedFrame()
    {
        if (_suppressCurrentDecodeOutput)
            return false;

        if (Volatile.Read(ref _queuedH264Frames) > MaxQueuedFramesForOutput)
            return false;

        return !_renderer.HasPendingFrame;
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
                UpdateDisplayImageLayout();
                StatusText.Text = $"{_decoderCodec} {_displayWidth}x{_displayHeight}";
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

    private void OnD3D11FrameDecoded(object? sender, D3D11DecodedFrame decodedFrame)
    {
        _hasDecodedFrame = true;

        if (decodedFrame.Width != _displayWidth || decodedFrame.Height != _displayHeight)
        {
            _displayWidth = decodedFrame.Width;
            _displayHeight = decodedFrame.Height;

            Dispatcher.Invoke(() =>
            {
                _renderer.Initialize(_displayWidth, _displayHeight);
                DisplayImage.Source = _renderer.Bitmap;
                UpdateDisplayImageLayout();
                StatusText.Text = $"{_decoderCodec} D3D11 {_displayWidth}x{_displayHeight}";
                StatusText.Visibility = Visibility.Collapsed;
            });
        }

        if (!_renderer.RenderD3D11Frame(decodedFrame))
            decodedFrame.Release();
    }

    private void OnRawFrame(object? sender, RawFramePayload frame)
    {
        _frameCount++;
        _totalBytes += frame.PixelData.Length;
        if (_initialRawFrameLogs < 3)
        {
            _initialRawFrameLogs++;
            DiagLog.Write($"RAW帧: idx={frame.FrameIndex}, bytes={frame.PixelData.Length}");
        }

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
                UpdateDisplayImageLayout();
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

    private void OnImageSourceChanged(object? sender, EventArgs e)
    {
        DisplayImage.Source = _renderer.Bitmap;
        UpdateDisplayImageLayout();
    }

    private void UpdateDisplayImageLayout()
    {
        if (_displayWidth <= 0 || _displayHeight <= 0)
            return;

        if (!DisplayImage.Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateDisplayImageLayout);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var hostWidth = DisplayHost.ActualWidth;
        var hostHeight = DisplayHost.ActualHeight;
        if (hostWidth <= 0 || hostHeight <= 0)
            return;

        var hostPhysicalWidth = hostWidth * dpi.DpiScaleX;
        var hostPhysicalHeight = hostHeight * dpi.DpiScaleY;
        var scale = Math.Min(hostPhysicalWidth / _displayWidth, hostPhysicalHeight / _displayHeight);
        if (scale <= 0)
            return;

        if (scale > 0.995)
            scale = 1.0;

        var physicalWidth = Math.Max(1, Math.Round(_displayWidth * scale));
        var physicalHeight = Math.Max(1, Math.Round(_displayHeight * scale));
        var pixelPerfect = (int)physicalWidth == _displayWidth && (int)physicalHeight == _displayHeight;
        RenderOptions.SetBitmapScalingMode(
            DisplayImage,
            pixelPerfect ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        DisplayImage.Width = physicalWidth / dpi.DpiScaleX;
        DisplayImage.Height = physicalHeight / dpi.DpiScaleY;
        LogDisplayLayoutIfNeeded(
            hostWidth,
            hostHeight,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            hostPhysicalWidth,
            hostPhysicalHeight,
            physicalWidth,
            physicalHeight,
            scale,
            pixelPerfect);
    }

    private void LogDisplayLayoutIfNeeded(
        double hostWidth,
        double hostHeight,
        double dpiScaleX,
        double dpiScaleY,
        double hostPhysicalWidth,
        double hostPhysicalHeight,
        double physicalWidth,
        double physicalHeight,
        double scale,
        bool pixelPerfect)
    {
        var roundedPhysicalWidth = (int)physicalWidth;
        var roundedPhysicalHeight = (int)physicalHeight;
        var now = DateTime.UtcNow;
        var layoutChanged =
            _lastLayoutPhysicalWidth != roundedPhysicalWidth ||
            _lastLayoutPhysicalHeight != roundedPhysicalHeight ||
            _lastLayoutPixelPerfect != pixelPerfect ||
            Math.Abs(_lastLayoutScale - scale) >= 0.005;

        if (!layoutChanged && (now - _lastLayoutLogTime).TotalSeconds < 5)
            return;

        _lastLayoutPhysicalWidth = roundedPhysicalWidth;
        _lastLayoutPhysicalHeight = roundedPhysicalHeight;
        _lastLayoutScale = scale;
        _lastLayoutPixelPerfect = pixelPerfect;
        _lastLayoutLogTime = now;

        DiagLog.Write(
            $"显示布局: video={_displayWidth}x{_displayHeight}, host={hostWidth:F1}x{hostHeight:F1}dip/{hostPhysicalWidth:F0}x{hostPhysicalHeight:F0}px, dpiScale={dpiScaleX:F2}x{dpiScaleY:F2}, output={roundedPhysicalWidth}x{roundedPhysicalHeight}px, scale={scale * 100:F1}%, pixelPerfect={pixelPerfect}");
    }

    protected override void OnClosed(EventArgs e)
    {
        _localCursorIdleTimer.Stop();
        ShowLocalCursor();
        _usb4IpDetectionCts?.Cancel();
        _decodeCts?.Cancel();
        _h264Signal.Release();
        _receiver.Disconnect();
        _h264Decoder?.Dispose();
        _renderer.Dispose();
        _loggerFactory.Dispose();
        base.OnClosed(e);
    }
}
