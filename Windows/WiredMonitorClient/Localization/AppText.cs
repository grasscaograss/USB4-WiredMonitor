using System.Globalization;

namespace WiredMonitorClient.Localization;

public enum UiLanguage
{
    English,
    Chinese,
}

public static class AppText
{
    public static UiLanguage Current { get; private set; } = DetectLanguage();

    public static bool IsChinese => Current == UiLanguage.Chinese;

    public static void Use(UiLanguage language)
    {
        Current = language;
    }

    public static UiLanguage FromCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DetectLanguage();

        var normalized = value.Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized is "zh" or "zh-cn" or "zh-hans" or "cn" or "chinese")
            return UiLanguage.Chinese;

        return UiLanguage.English;
    }

    public static string WindowTitle => IsChinese
        ? "Wired Monitor - 扩展屏客户端"
        : "Wired Monitor - External Display Client";

    public static string MacAddressLabel => IsChinese ? "Mac 地址:" : "Mac address:";
    public static string PortLabel => IsChinese ? "端口:" : "Port:";
    public static string LanguageLabel => IsChinese ? "语言:" : "Language:";
    public static string ConnectButton => IsChinese ? "连接" : "Connect";
    public static string DisconnectButton => IsChinese ? "断开" : "Disconnect";
    public static string FullscreenButton => IsChinese ? "全屏" : "Fullscreen";
    public static string ExitFullscreenButton => IsChinese ? "退出全屏" : "Exit full screen";
    public static string WindowsControlButton => IsChinese ? "Windows 控制" : "Windows Control";
    public static string WindowsControlActiveButton => IsChinese ? "正在控制" : "Controlling";
    public static string Connected => IsChinese ? "已连接" : "Connected";
    public static string NotConnected => IsChinese ? "未连接" : "Disconnected";
    public static string NotConnectedPrompt => IsChinese
        ? "未连接 - 请输入 Mac 的 Thunderbolt IP 地址"
        : "Not connected - enter the Mac Thunderbolt IP address";

    public static string Usb4Detecting => IsChinese ? "USB4IP: 检测中" : "USB4IP: detecting";
    public static string Usb4NotFound => IsChinese ? "USB4IP: 未找到" : "USB4IP: not found";
    public static string Usb4Error => IsChinese ? "USB4IP: 检测异常" : "USB4IP: detection error";

    public static string ErrorTitle => IsChinese ? "错误" : "Error";
    public static string WaitingVideoData => IsChinese ? "等待画面数据..." : "Waiting for video data...";
    public static string DetectingMacUsb4Ip => IsChinese ? "正在检测 Mac USB4 IP..." : "Detecting Mac USB4 IP...";
    public static string MacUsb4IpNotDetected => IsChinese ? "没有检测到 Mac USB4 IP" : "Mac USB4 IP was not detected";
    public static string ConnectionFailedShort => IsChinese ? "连接失败" : "Connection failed";
    public static string WindowsControlHotkeyHint => IsChinese
        ? "请在 Mac 端按 Ctrl+Option+Command+W 进入或退出 Windows 控制模式"
        : "Press Ctrl+Option+Command+W on the Mac to enter or exit Windows control mode";

    public static string Usb4Host(string host) => $"USB4IP: {host}";

    public static string Connecting(string host, int port) => IsChinese
        ? $"正在连接 {host}:{port}..."
        : $"Connecting to {host}:{port}...";

    public static string ConnectionFailed(string message) => IsChinese
        ? $"连接失败: {message}"
        : $"Connection failed: {message}";

    public static string ReceiveInterrupted(string? message) => IsChinese
        ? $"接收中断: {message}"
        : $"Receive interrupted: {message}";

    public static string AutoReconnecting(int attempt) => IsChinese
        ? $"连接中断，正在自动重连 #{attempt}..."
        : $"Connection interrupted, reconnecting #{attempt}...";

    public static string AutoReconnectFailed(string message) => IsChinese
        ? $"自动重连失败，继续重试: {message}"
        : $"Auto reconnect failed, retrying: {message}";

    public static string DetectedMacUsb4Ip(string host) => IsChinese
        ? $"检测到 Mac USB4 IP: {host}"
        : $"Detected Mac USB4 IP: {host}";

    public static string ReceivedKeyFrame(string codecName, ulong frameIndex) => IsChinese
        ? $"收到 {codecName} 关键帧 #{frameIndex}，正在解码..."
        : $"Received {codecName} key frame #{frameIndex}, decoding...";

    public static string ReceivedFrameWaitingDecoder(string codecName, ulong frameIndex) => IsChinese
        ? $"收到 {codecName} 帧 #{frameIndex}，等待解码输出..."
        : $"Received {codecName} frame #{frameIndex}, waiting for decoder output...";

    public static string ReceivingNoDecoder(int fpsCount, long totalKb) => IsChinese
        ? $"接收中 (无解码器) FPS: {fpsCount} 累计: {totalKb}KB"
        : $"Receiving (no decoder) FPS: {fpsCount} total: {totalKb}KB";

    public static string HardwareDecoderInitFailed(string codec) => IsChinese
        ? $"{codec} 硬件解码器初始化失败 - 需要支持 D3D11VA/DXVA2 的 GPU 和 FFmpeg 库"
        : $"{codec} hardware decoder initialization failed - requires a GPU and FFmpeg build with D3D11VA/DXVA2 support";

    public static string DecoderUnavailable(string message) => IsChinese
        ? $"解码器不可用: {message}"
        : $"Decoder unavailable: {message}";

    public static string Bandwidth(double mbps) => IsChinese
        ? $"带宽: {mbps:F1} Mbps"
        : $"Bandwidth: {mbps:F1} Mbps";

    private static UiLanguage DetectLanguage()
    {
        var env = Environment.GetEnvironmentVariable("WIRED_MONITOR_LANG");
        if (!string.IsNullOrWhiteSpace(env))
            return FromCode(env);

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Chinese
            : UiLanguage.English;
    }
}
