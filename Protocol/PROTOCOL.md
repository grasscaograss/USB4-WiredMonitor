# Wired Monitor Protocol

## Overview

Wired Monitor 通过 TCP/IP over Thunderbolt/USB4 在 macOS 创建虚拟显示器，并将该虚拟显示器画面实时传输到 Windows，实现 Windows 屏幕作为 Mac 副屏。

## Transport

- **协议**: TCP over Thunderbolt Networking
- **端口**: 9802 (视频流与当前 HELLO 握手), 9801 (预留控制通道)
- **字节序**: Little Endian

## Packet Format

所有数据包共享统一头部：

```
┌──────────┬──────────┬──────────┬──────────────┐
│ Magic    │ Version  │ Type     │ Payload Len  │
│ 2 bytes  │ 2 bytes  │ 2 bytes  │ 4 bytes      │
│ 0xWM     │ 0x0001   │ uint16   │ uint32       │
└──────────┴──────────┴──────────┴──────────────┘
Total header: 10 bytes
```

视频宽高默认只按 2 像素对齐，避免对 Retina/高 DPI 屏幕做 16 像素裁剪后再缩放；可通过 `WIRED_MONITOR_ALIGN` 覆盖。

## Packet Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| HELLO | 0x0001 | C→S | 客户端连接握手，携带 Windows 显示信息 |
| HELLO_ACK | 0x0002 | S→C | 服务端确认 |
| DISPLAY_INFO | 0x0010 | S→C | 显示器配置信息 |
| FRAME_REQUEST | 0x0020 | C→S | 请求视频帧 |
| FRAME_H264 | 0x0030 | S→C | H.264 编码帧 |
| FRAME_RAW | 0x0031 | S→C | 原始 BGRA 帧 |
| FRAME_HEVC | 0x0032 | S→C | HEVC/H.265 编码帧 |
| INPUT_EVENT | 0x0040 | C→S | 鼠标/键盘输入事件 |
| STATS | 0x0050 | 双向 | 性能统计 |
| CURSOR_POSITION | 0x0060 | S→C | 独立鼠标位置更新 |

默认鼠标指针直接包含在视频帧中，以保证位置由 macOS 负责合成；独立鼠标通道仅在 `WIRED_MONITOR_CAPTURE_CURSOR=0` 且 `WIRED_MONITOR_SEPARATE_CURSOR=1` 时启用。

## Packet Details

### HELLO (0x0001)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┐
│ ClientWidth  │ ClientHeight │ RefreshRate  │ Dpi          │
│ uint32       │ uint32       │ uint32       │ uint32       │
│ pixels       │ pixels       │ Hz           │ dpi          │
└──────────────┴──────────────┴──────────────┴──────────────┘
```

### HELLO_ACK (0x0002)
```
Payload:
┌──────────────┬──────────────┐
│ ServerWidth  │ ServerHeight │
│ uint32       │ uint32       │
└──────────────┴──────────────┘
```

### DISPLAY_INFO (0x0010)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┐
│ Width        │ Height       │ RefreshRate  │ Codec        │
│ uint32       │ uint32       │ uint32       │ uint8        │
│              │              │ Hz           │ 0=Raw,1=H264 │
│              │              │              │ 2=HEVC       │
└──────────────┴──────────────┴──────────────┘
```

### FRAME_H264 (0x0030)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ IsKeyFrame   │ Width        │ Height       │ NAL Data     │
│ uint64       │ uint64       │ uint8        │ uint32       │ uint32       │ bytes...     │
│              │ ms           │ 0/1          │ pixels       │ pixels       │ Annex-B H264 │
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴──────────────┘
```

### FRAME_HEVC (0x0032)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ IsKeyFrame   │ Width        │ Height       │ NAL Data     │
│ uint64       │ uint64       │ uint8        │ uint32       │ uint32       │ bytes...     │
│              │ ms           │ 0/1          │ pixels       │ pixels       │ Annex-B HEVC │
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴──────────────┘
```

### FRAME_RAW (0x0031)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ Width        │ Height       │ BytesPerRow  │ BGRA Data    │
│ uint64       │ uint64       │ uint32       │ uint32       │ uint32       │ bytes...     │
│              │ ms           │ pixels       │ pixels       │ bytes        │              │
└──────────────┴──────────────┴──────────────┴──────────────┴──────────────┴──────────────┘
```

### INPUT_EVENT (0x0040)
```
Payload:
┌──────────────┬──────────────┐
│ EventType    │ EventData    │
│ uint8        │ bytes...     │
│              │              │
└──────────────┴──────────────┘

EventType:
  0x01 = MouseMove (x: int32, y: int32)
  0x02 = MouseDown (button: uint8, x: int32, y: int32)
  0x03 = MouseUp   (button: uint8, x: int32, y: int32)
  0x04 = KeyDown   (keyCode: uint16)
  0x05 = KeyUp     (keyCode: uint16)
  0x06 = Scroll    (deltaX: int32, deltaY: int32)
```

### CURSOR_POSITION (0x0060)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┐
│ Timestamp    │ X            │ Y            │ Visible      │
│ uint64       │ uint32       │ uint32       │ uint8        │
│ ms           │ pixels       │ pixels       │ 0/1          │
└──────────────┴──────────────┴──────────────┴──────────────┘
```

## Connection Flow

```
Windows (Client)                    Mac (Server)
     │                                  │
     │──── TCP Connect (port 9802) ────>│
     │──── HELLO ──────────────────────>│
     │      Mac creates virtual display │
     │<─── FRAME_H264 ─────────────────│
     │<─── FRAME_H264 ─────────────────│
     │<─── FRAME_H264 ─────────────────│
     │                                  │
     │──── INPUT_EVENT ────────────────>│
     │                                  │
```
