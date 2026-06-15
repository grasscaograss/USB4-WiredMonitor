# USB4 Wired Monitor

[English](README.md) | [简体中文](README.zh-CN.md)

USB4 Wired Monitor turns a Windows PC into a low-latency external display for a
Mac over a direct USB4/Thunderbolt cable.

The project is built around the network interface that macOS and Windows expose
for Thunderbolt/USB4 connections. The Mac creates or captures a display, encodes
it with VideoToolbox, and sends the video stream over TCP. The Windows client
receives the stream, decodes it with FFmpeg hardware acceleration, and presents
it in a WPF window.

This is an experimental MVP. It is intended for local development, latency
tuning, and hardware validation, not as a polished commercial display product.

## Highlights

- Direct TCP video stream over USB4/Thunderbolt networking.
- macOS server in Swift with CGDisplayStream/ScreenCaptureKit and VideoToolbox.
- Windows client in C# WPF with FFmpeg D3D11VA/DXVA2 hardware decoding.
- Optional D3D11 direct rendering path on Windows.
- Automatic Mac USB4 IP detection in the Windows client.
- Optional macOS virtual display creation through `CGVirtualDisplay` runtime
  classes.
- Windows Control Mode toggled with `Ctrl+Option+Command+W`, so the Mac
  keyboard and mouse can temporarily control the real Windows desktop.
- English and Simplified Chinese Windows UI.
- Standalone network throughput and latency test utility.

## Current Limitations

- Windows Control Mode targets the normal Windows desktop and normal apps. It
  does not guarantee control over elevated windows or the UAC secure desktop.
- Virtual display creation depends on macOS runtime support for
  `CGVirtualDisplay`. If it is unavailable, use main-display mirroring mode.
- The Windows client intentionally requires hardware decoding; software decode
  fallback is rejected to keep latency predictable.
- FFmpeg DLLs are not bundled in this repository.

## Repository Layout

```text
Mac/WiredMonitorServer/       macOS Swift server
Windows/WiredMonitorClient/   Windows WPF client
NetworkTest/                  TCP throughput and latency test utility
Protocol/                     Wire protocol documentation
```

## Requirements

### macOS

- macOS 13 or later.
- Xcode Command Line Tools.
- Swift Package Manager.
- Screen Recording permission for the terminal app used to start the server.
- Accessibility and Input Monitoring permissions for the terminal app used to
  start the server when Windows Control Mode is needed.
- A USB4/Thunderbolt port and cable.

### Windows

- Windows 10/11.
- .NET 9 SDK for `Windows/WiredMonitorClient`.
- .NET 10 SDK for `NetworkTest`.
- A GPU and driver supported by FFmpeg D3D11VA or DXVA2 hardware decoding.
- FFmpeg shared native libraries.
- A USB4/Thunderbolt port and cable.

## Install FFmpeg On Windows

The Windows client uses the `FFmpeg.AutoGen` NuGet package, but the FFmpeg native
shared libraries must still be available at runtime.

1. Download a Windows shared FFmpeg build, for example from
   [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases).
2. Extract the archive.
3. Copy the FFmpeg DLLs into the built client output folder:

```text
Windows/WiredMonitorClient/bin/<Configuration>/net9.0-windows/ffmpeg/
```

For development, `<Configuration>` is usually `Debug`.

## Quick Start

### 1. Connect The Machines

Connect the Mac and Windows PC with a USB4/Thunderbolt cable.

On macOS, check:

```text
System Settings -> Network -> Thunderbolt Bridge
```

On Windows, check:

```text
Settings -> Network & Internet -> Advanced network settings
```

The two machines usually receive link-local IPv4 addresses such as
`169.254.x.x`.

### 2. Start The Mac Server

```bash
cd Mac/WiredMonitorServer
swift run
```

The server listens on TCP port `9802`.

### 3. Start The Windows Client

```powershell
cd Windows\WiredMonitorClient
dotnet run
```

You can leave the Mac address empty and click **Connect**; the client will try
to find the Mac service over USB4 automatically. You can also type the Mac
Thunderbolt Bridge IP manually.

## Windows Control Mode

When the Mac pointer is on the Windows external-display area and you need to
briefly use the real Windows PC, press this hotkey on the Mac:

```text
Ctrl+Option+Command+W
```

The Windows client hides the external-display window, Mac keyboard/mouse input
is forwarded over the same USB4/TB TCP connection, and Windows injects it into
the normal desktop with `SendInput`. Press the same hotkey again to exit control
mode and restore the Wired Monitor window.

The default key mapping follows Mac habits: `Command` maps to Windows `Ctrl`,
and `Option` maps to `Alt`, so `Command+C/V/A/Z` works as Windows copy, paste,
select all, and undo.

On first use, macOS may require Accessibility and Input Monitoring permissions
for the terminal/app running `swift run`. Without those permissions, video still
works but control mode is disabled. The first implementation is intended for the
normal Windows desktop and normal applications, not the UAC secure desktop.

## Language

The Windows UI and Mac server logs follow the operating system language by
default. They currently support English and Simplified Chinese.

You can switch language in the toolbar, or force a language before launch:

```powershell
$env:WIRED_MONITOR_LANG="en-US"
dotnet run --project Windows\WiredMonitorClient
```

```powershell
$env:WIRED_MONITOR_LANG="zh-CN"
dotnet run --project Windows\WiredMonitorClient
```

## Network Test

Use `NetworkTest` before video debugging to confirm the USB4/TB link is healthy.

Start the test server on one machine:

```bash
dotnet run --project NetworkTest -- server
```

Discover candidate USB4/Thunderbolt interfaces on Windows:

```powershell
dotnet run --project NetworkTest -- discovery
```

Run throughput test:

```powershell
dotnet run --project NetworkTest -- client <Mac-IP>
```

Run latency test:

```powershell
dotnet run --project NetworkTest -- ping <Mac-IP>
```

## Runtime Options

Set these environment variables before starting the corresponding process.

### Mac Server

| Variable | Default | Description |
| --- | --- | --- |
| `WIRED_MONITOR_LANG` | System language | `en-US` or `zh-CN`; controls Mac server log language. |
| `WIRED_MONITOR_FPS` | Client refresh rate, capped at 60 | Stream FPS, max 120. |
| `WIRED_MONITOR_BITRATE` | Auto | Video bitrate in bits per second. |
| `WIRED_MONITOR_CODEC` | `h264` | `h264` or `hevc`. |
| `WIRED_MONITOR_QUALITY` | `1.0` | VideoToolbox quality value. |
| `WIRED_MONITOR_SCALE` | `1` | Capture scale, `0 < value <= 1`. |
| `WIRED_MONITOR_CAPTURE` | `cgstream` | Capture mode: `cgstream`, `sck`, or `image`. |
| `WIRED_MONITOR_CAPTURE_CURSOR` | `1` | Include the macOS cursor in video frames. |
| `WIRED_MONITOR_VIRTUAL_DISPLAY` | `1` | Enable virtual display creation. |
| `WIRED_MONITOR_MIRROR_MAIN` | `0` | Set to `1` to stream the main display instead. |
| `WIRED_MONITOR_VIRTUAL_WIDTH` | Client width | Override virtual display pixel width. |
| `WIRED_MONITOR_VIRTUAL_HEIGHT` | Client height | Override virtual display pixel height. |
| `WIRED_MONITOR_RETINA` | Auto | Force HiDPI virtual display mode. |

Example:

```bash
cd Mac/WiredMonitorServer
WIRED_MONITOR_FPS=60 WIRED_MONITOR_BITRATE=120000000 swift run
```

### Windows Client

| Variable | Default | Description |
| --- | --- | --- |
| `WIRED_MONITOR_LANG` | System UI language | `en-US` or `zh-CN`. |
| `WIRED_MONITOR_HWDEC` | Auto | Force `d3d11va` or `dxva2`. |
| `WIRED_MONITOR_RENDERER` | Auto | Set to `wpf` or `writeablebitmap` to bypass D3DImage. |
| `WIRED_MONITOR_D3D11_DIRECT` | Auto | Set to `0` to disable D3D11 direct output. |
| `WIRED_MONITOR_D3D11_COLOR_RANGE` | Limited | Set to `full` for full-range YUV conversion. |
| `WIRED_MONITOR_CLIENT_WIDTH` | Current display | Override HELLO client width. |
| `WIRED_MONITOR_CLIENT_HEIGHT` | Current display | Override HELLO client height. |
| `WIRED_MONITOR_CLIENT_REFRESH` | Current display | Override reported refresh rate. |
| `WIRED_MONITOR_CLIENT_DPI` | System DPI | Override reported DPI. |

FFmpeg self-test:

```powershell
dotnet run --project Windows\WiredMonitorClient -- --ffmpeg-self-test
```

Receive probe:

```powershell
dotnet run --project Windows\WiredMonitorClient -- --probe <Mac-IP> 9802
```

## Troubleshooting

- Cannot connect: run `NetworkTest discovery` and confirm both machines have
  USB4/Thunderbolt IPv4 addresses.
- Black screen: confirm FFmpeg DLLs are inside the `ffmpeg/` folder under the
  built Windows output directory.
- Hardware decode failure: try forcing the decoder:

```powershell
$env:WIRED_MONITOR_HWDEC="d3d11va"
dotnet run --project Windows\WiredMonitorClient
```

- Wrong display captured on macOS: check the Mac server logs for the selected
  display ID and avoid capture fallback unless you need it.
- Virtual display unavailable: use main-display mirroring mode:

```bash
WIRED_MONITOR_MIRROR_MAIN=1 swift run
```

## Protocol

See [Protocol/PROTOCOL.md](Protocol/PROTOCOL.md) for packet formats and message
types.

## License

This project's own source code is licensed under the [MIT License](LICENSE).

Third-party dependencies keep their own licenses:

- FFmpeg and FFmpeg builds are licensed separately by their upstream projects and
  build distributors.
- `FFmpeg.AutoGen` is a third-party NuGet package with its own license.
- Apple frameworks and Microsoft/.NET components are governed by their
  respective platform licenses.

This repository does not redistribute FFmpeg binaries. If you publish a binary
release that includes FFmpeg DLLs, make sure that release complies with the
license terms of the FFmpeg build you ship.
