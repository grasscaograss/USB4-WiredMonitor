# USB4 Wired Monitor

USB4 Wired Monitor uses USB4/Thunderbolt networking to use a Windows PC as an
external display for a Mac.

The Mac side creates or captures a display, encodes frames with VideoToolbox,
and streams them over TCP. The Windows side receives the stream, decodes it with
FFmpeg hardware decoding, and renders it in a WPF window.

> Status: experimental MVP. It is useful for local testing and development, but
> it is not yet a polished replacement for a commercial remote display product.

## Features

- TCP video stream over USB4/Thunderbolt networking.
- Mac server written in Swift with CGDisplayStream/ScreenCaptureKit and
  VideoToolbox.
- Windows client written in C# WPF with FFmpeg hardware decoding and D3D11
  rendering.
- Automatic USB4 IP detection on Windows.
- Optional macOS virtual display creation.
- Network throughput and latency test utility.

## Repository Layout

```text
Mac/WiredMonitorServer/       macOS Swift server
Windows/WiredMonitorClient/   Windows WPF client
NetworkTest/                  TCP throughput and latency test utility
Protocol/                     Wire protocol documentation
```

## Requirements

### Mac

- macOS 13 or later.
- Xcode Command Line Tools.
- Swift Package Manager.
- Screen Recording permission for the terminal/app that starts the server.

### Windows

- Windows 10/11.
- .NET 9 SDK for `Windows/WiredMonitorClient`.
- .NET 10 SDK for `NetworkTest`.
- A GPU/driver combination supported by FFmpeg D3D11VA or DXVA2 hardware
  decoding.
- FFmpeg shared native libraries.

### Cable And Network

- A USB4/Thunderbolt cable between the Mac and Windows machines.
- Thunderbolt/USB4 networking enabled on both systems.
- The machines usually receive link-local IPv4 addresses such as
  `169.254.x.x`.

## Install Dependencies

### Windows FFmpeg Runtime

The Windows client uses `FFmpeg.AutoGen`, but it still needs FFmpeg native
shared libraries at runtime.

1. Download a Windows shared FFmpeg build, for example from
   [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases).
2. Extract the build.
3. Copy the required DLLs into:

```text
Windows/WiredMonitorClient/bin/<Configuration>/net9.0-windows/ffmpeg/
```

During development, `<Configuration>` is usually `Debug`.

This repository does not redistribute FFmpeg binaries. If you package and
redistribute FFmpeg with the app, you must comply with the license terms of the
FFmpeg build you ship.

## Quick Start

### 1. Connect USB4/Thunderbolt

Connect the Mac and Windows machines with a USB4/Thunderbolt cable.

On macOS, check:

```text
System Settings -> Network -> Thunderbolt Bridge
```

On Windows, check:

```text
Settings -> Network & Internet -> Advanced network settings
```

### 2. Start The Mac Server

```bash
cd Mac/WiredMonitorServer
swift run
```

The server listens on TCP port `9802` for the video stream.

### 3. Start The Windows Client

```powershell
cd Windows\WiredMonitorClient
dotnet run
```

You can leave the Mac address field empty and click `连接`; the Windows client
will try to detect the Mac over USB4 automatically. You can also enter the Mac
Thunderbolt Bridge IP manually.

## Network Test

The network test utility is useful before debugging video latency.

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

## Useful Runtime Options

Set these environment variables before starting the corresponding process.

### Mac Server

| Variable | Default | Description |
| --- | --- | --- |
| `WIRED_MONITOR_FPS` | Client refresh rate, capped at 60 | Stream FPS, max 120. |
| `WIRED_MONITOR_BITRATE` | Auto | Video bitrate in bits per second. |
| `WIRED_MONITOR_CODEC` | `h264` | `h264` or `hevc`. |
| `WIRED_MONITOR_QUALITY` | `1.0` | VideoToolbox quality value. |
| `WIRED_MONITOR_SCALE` | `1` | Capture scale, `0 < value <= 1`. |
| `WIRED_MONITOR_CAPTURE` | `cgstream` | Capture mode: `cgstream`, `sck`, or `image`. |
| `WIRED_MONITOR_CAPTURE_CURSOR` | `1` | Include macOS cursor in video frames. |
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
| `WIRED_MONITOR_HWDEC` | Auto | Force `d3d11va` or `dxva2`. |
| `WIRED_MONITOR_RENDERER` | Auto | Renderer selection. |
| `WIRED_MONITOR_D3D11_DIRECT` | Auto | Enable or disable D3D11 direct path. |
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

- If the Windows client cannot connect, run `NetworkTest discovery` and confirm
  both machines have USB4/Thunderbolt IPv4 addresses.
- If video is black, confirm FFmpeg DLLs are in the `ffmpeg/` folder under the
  built Windows output directory.
- If decoding fails, try forcing a hardware decoder:

```powershell
$env:WIRED_MONITOR_HWDEC="d3d11va"
dotnet run --project Windows\WiredMonitorClient
```

- If macOS captures the wrong display, disable fallback capture and check the
  server logs for the selected display ID.
- If the Mac server cannot create a virtual display, try:

```bash
WIRED_MONITOR_MIRROR_MAIN=1 swift run
```

## Protocol

See [Protocol/PROTOCOL.md](Protocol/PROTOCOL.md) for packet formats and message
types.

## License

This project's own source code is licensed under the MIT License. See
[LICENSE](LICENSE).

Third-party dependencies keep their own licenses:

- FFmpeg and FFmpeg builds are licensed separately by their upstream projects and
  build distributors.
- `FFmpeg.AutoGen` is a third-party NuGet package with its own license.
- Apple frameworks and Microsoft/.NET components are governed by their
  respective platform licenses.

If you redistribute binaries, especially binaries that include FFmpeg DLLs, make
sure your distribution complies with all applicable third-party license terms.
