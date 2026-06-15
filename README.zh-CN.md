# USB4 Wired Monitor

[English](README.md) | [简体中文](README.zh-CN.md)

USB4 Wired Monitor 通过一根 USB4/Thunderbolt 线，把 Windows 电脑变成 Mac 的低延迟扩展屏。

项目利用 macOS 和 Windows 在 Thunderbolt/USB4 连接上暴露出来的网络接口工作：Mac 端创建或捕获显示器画面，使用 VideoToolbox 编码，再通过 TCP 发送；Windows 端接收视频流，使用 FFmpeg 硬件解码，并在 WPF 窗口中显示。

当前仍是实验性 MVP，适合本地开发、延迟调优和硬件验证，还不是成熟商业远程显示软件的替代品。

## 功能亮点

- 基于 USB4/Thunderbolt Networking 的直连 TCP 视频流。
- Mac 端使用 Swift、CGDisplayStream/ScreenCaptureKit 和 VideoToolbox。
- Windows 端使用 C# WPF、FFmpeg D3D11VA/DXVA2 硬件解码。
- Windows 端可选 D3D11 直通渲染路径。
- Windows 客户端自动检测 Mac USB4 IP。
- 可选通过 `CGVirtualDisplay` 运行时类创建 macOS 虚拟显示器。
- 可用 `Ctrl+Option+Command+W` 切换 Windows 控制模式，用 Mac 键鼠临时操作真实 Windows 桌面。
- Windows 客户端支持英文和简体中文界面。
- 独立的网络吞吐和延迟测试工具。

## 当前限制

- Windows 控制模式仅面向普通桌面和普通应用，不保证控制管理员窗口或 UAC 安全桌面。
- 虚拟显示器依赖 macOS 运行时是否提供 `CGVirtualDisplay`。如果不可用，可以使用主屏镜像模式。
- Windows 客户端为了稳定低延迟，刻意要求硬件解码，不回退到软件解码。
- 仓库不内置 FFmpeg DLL。

## 目录结构

```text
Mac/WiredMonitorServer/       macOS Swift 服务端
Windows/WiredMonitorClient/   Windows WPF 客户端
NetworkTest/                  TCP 吞吐和延迟测试工具
Protocol/                     通信协议文档
```

## 环境要求

### macOS

- macOS 13 或更新版本。
- Xcode Command Line Tools。
- Swift Package Manager。
- 启动服务端的终端/App 需要屏幕录制权限。
- 使用 Windows 控制模式时，启动服务端的终端/App 还需要辅助功能和输入监控权限。
- USB4/Thunderbolt 接口和线缆。

### Windows

- Windows 10/11。
- 用于 `Windows/WiredMonitorClient` 的 .NET 9 SDK。
- 用于 `NetworkTest` 的 .NET 10 SDK。
- 支持 FFmpeg D3D11VA 或 DXVA2 硬件解码的 GPU 和驱动。
- FFmpeg shared native libraries。
- USB4/Thunderbolt 接口和线缆。

## 安装 Windows 端 FFmpeg

Windows 客户端使用 `FFmpeg.AutoGen` NuGet 包，但运行时仍需要 FFmpeg native DLL。

1. 下载 Windows shared FFmpeg 构建，例如
   [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases)。
2. 解压下载的压缩包。
3. 把 FFmpeg DLL 复制到客户端构建输出目录：

```text
Windows/WiredMonitorClient/bin/<Configuration>/net9.0-windows/ffmpeg/
```

开发时 `<Configuration>` 通常是 `Debug`。

## 快速开始

### 1. 连接两台电脑

用 USB4/Thunderbolt 线连接 Mac 和 Windows 电脑。

macOS 端检查：

```text
系统设置 -> 网络 -> Thunderbolt Bridge
```

Windows 端检查：

```text
设置 -> 网络和 Internet -> 高级网络设置
```

两台机器通常会获得类似 `169.254.x.x` 的链路本地 IPv4 地址。

### 2. 启动 Mac 服务端

```bash
cd Mac/WiredMonitorServer
swift run
```

服务端默认监听 TCP `9802` 端口。

### 3. 启动 Windows 客户端

```powershell
cd Windows\WiredMonitorClient
dotnet run
```

可以留空 Mac 地址，直接点击 **连接**；客户端会尝试通过 USB4 自动发现 Mac 服务端。也可以手动输入 Mac 的 Thunderbolt Bridge IP。

## Windows 控制模式

当 Mac 鼠标已经移动到 Windows 这块扩展屏上，又需要临时操作真实 Windows 桌面时，可以在 Mac 端按：

```text
Ctrl+Option+Command+W
```

进入控制模式后，Windows 客户端会隐藏扩展屏窗口，Mac 键鼠会通过同一条 USB4/TB TCP 连接转发到 Windows，并由 Windows 端通过 `SendInput` 注入普通桌面。再次按同一热键会退出控制模式并恢复 Wired Monitor 窗口。

键位默认采用 Mac 使用习惯：`Command` 映射为 Windows `Ctrl`，`Option` 映射为 `Alt`。因此 `Command+C/V/A/Z` 会按 Windows 的复制、粘贴、全选、撤销执行。

首次使用时，macOS 可能要求给启动 `swift run` 的终端/App 授予辅助功能和输入监控权限。未授权时，视频扩展屏仍可工作，但控制模式不会启用。Windows 端第一版只保证普通桌面和普通应用，不保证 UAC 安全桌面。

## 语言

Windows 客户端界面和 Mac 服务端日志默认跟随系统语言，目前支持英文和简体中文。

你可以在工具栏里切换语言，也可以在启动前通过环境变量强制指定：

```powershell
$env:WIRED_MONITOR_LANG="en-US"
dotnet run --project Windows\WiredMonitorClient
```

```powershell
$env:WIRED_MONITOR_LANG="zh-CN"
dotnet run --project Windows\WiredMonitorClient
```

## 网络测试

调试视频延迟之前，建议先用 `NetworkTest` 确认 USB4/TB 链路正常。

在其中一台机器上启动测试服务端：

```bash
dotnet run --project NetworkTest -- server
```

Windows 端发现候选 USB4/Thunderbolt 网络接口：

```powershell
dotnet run --project NetworkTest -- discovery
```

吞吐测试：

```powershell
dotnet run --project NetworkTest -- client <Mac-IP>
```

延迟测试：

```powershell
dotnet run --project NetworkTest -- ping <Mac-IP>
```

## 运行参数

在启动对应进程前设置这些环境变量。

### Mac 服务端

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `WIRED_MONITOR_LANG` | 系统语言 | `en-US` 或 `zh-CN`；控制 Mac 服务端日志语言。 |
| `WIRED_MONITOR_FPS` | 客户端刷新率，最高 60 | 视频流 FPS，最高 120。 |
| `WIRED_MONITOR_BITRATE` | 自动 | 视频码率，单位 bit/s。 |
| `WIRED_MONITOR_CODEC` | `h264` | `h264` 或 `hevc`。 |
| `WIRED_MONITOR_QUALITY` | `1.0` | VideoToolbox quality 值。 |
| `WIRED_MONITOR_SCALE` | `1` | 捕获缩放，范围 `0 < value <= 1`。 |
| `WIRED_MONITOR_CAPTURE` | `cgstream` | 捕获模式：`cgstream`、`sck` 或 `image`。 |
| `WIRED_MONITOR_CAPTURE_CURSOR` | `1` | 是否把 macOS 鼠标包含在视频帧里。 |
| `WIRED_MONITOR_VIRTUAL_DISPLAY` | `1` | 是否启用虚拟显示器创建。 |
| `WIRED_MONITOR_MIRROR_MAIN` | `0` | 设为 `1` 时改为推送主屏画面。 |
| `WIRED_MONITOR_VIRTUAL_WIDTH` | 客户端宽度 | 覆盖虚拟显示器像素宽度。 |
| `WIRED_MONITOR_VIRTUAL_HEIGHT` | 客户端高度 | 覆盖虚拟显示器像素高度。 |
| `WIRED_MONITOR_RETINA` | 自动 | 强制 HiDPI 虚拟显示模式。 |

示例：

```bash
cd Mac/WiredMonitorServer
WIRED_MONITOR_FPS=60 WIRED_MONITOR_BITRATE=120000000 swift run
```

### Windows 客户端

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `WIRED_MONITOR_LANG` | 系统界面语言 | `en-US` 或 `zh-CN`。 |
| `WIRED_MONITOR_HWDEC` | 自动 | 强制 `d3d11va` 或 `dxva2`。 |
| `WIRED_MONITOR_RENDERER` | 自动 | 设为 `wpf` 或 `writeablebitmap` 可绕过 D3DImage。 |
| `WIRED_MONITOR_D3D11_DIRECT` | 自动 | 设为 `0` 禁用 D3D11 直通输出。 |
| `WIRED_MONITOR_D3D11_COLOR_RANGE` | Limited | 设为 `full` 使用 full-range YUV 转换。 |
| `WIRED_MONITOR_CLIENT_WIDTH` | 当前显示器 | 覆盖 HELLO 握手中的客户端宽度。 |
| `WIRED_MONITOR_CLIENT_HEIGHT` | 当前显示器 | 覆盖 HELLO 握手中的客户端高度。 |
| `WIRED_MONITOR_CLIENT_REFRESH` | 当前显示器 | 覆盖上报刷新率。 |
| `WIRED_MONITOR_CLIENT_DPI` | 系统 DPI | 覆盖上报 DPI。 |

FFmpeg 自检：

```powershell
dotnet run --project Windows\WiredMonitorClient -- --ffmpeg-self-test
```

接收探测：

```powershell
dotnet run --project Windows\WiredMonitorClient -- --probe <Mac-IP> 9802
```

## 排障

- 无法连接：运行 `NetworkTest discovery`，确认两台机器都有 USB4/Thunderbolt IPv4 地址。
- 黑屏：确认 FFmpeg DLL 已放在 Windows 构建输出目录下的 `ffmpeg/` 文件夹。
- 硬件解码失败：尝试强制指定解码器：

```powershell
$env:WIRED_MONITOR_HWDEC="d3d11va"
dotnet run --project Windows\WiredMonitorClient
```

- macOS 抓错显示器：查看 Mac 服务端日志中的 display ID，除非确实需要，否则不要启用 capture fallback。
- 虚拟显示器不可用：使用主屏镜像模式：

```bash
WIRED_MONITOR_MIRROR_MAIN=1 swift run
```

## 协议

数据包格式和消息类型见 [Protocol/PROTOCOL.md](Protocol/PROTOCOL.md)。

## 开源协议

本项目自有源码使用 [MIT License](LICENSE)。

第三方依赖保留各自的许可证：

- FFmpeg 和 FFmpeg 构建由其上游项目和构建分发方单独授权。
- `FFmpeg.AutoGen` 是第三方 NuGet 包，遵循其自身许可证。
- Apple frameworks 和 Microsoft/.NET 组件遵循各自平台许可证。

本仓库不分发 FFmpeg 二进制文件。如果你发布包含 FFmpeg DLL 的二进制版本，需要确保该发布物符合所使用 FFmpeg 构建的许可证要求。
