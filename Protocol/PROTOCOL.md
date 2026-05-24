# Wired Monitor Protocol

## Overview

Wired Monitor 通过 TCP/IP over Thunderbolt/USB4 将 Mac 屏幕画面实时传输到 Windows，实现扩展屏功能。

## Transport

- **协议**: TCP over Thunderbolt Networking
- **端口**: 9801 (控制), 9802 (视频流)
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

## Packet Types

| Type | Value | Direction | Description |
|------|-------|-----------|-------------|
| HELLO | 0x0001 | C→S | 客户端连接握手 |
| HELLO_ACK | 0x0002 | S→C | 服务端确认 |
| DISPLAY_INFO | 0x0010 | S→C | 显示器配置信息 |
| FRAME_REQUEST | 0x0020 | C→S | 请求视频帧 |
| FRAME_H264 | 0x0030 | S→C | H.264 编码帧 |
| FRAME_RAW | 0x0031 | S→C | 原始 BGRA 帧 |
| INPUT_EVENT | 0x0040 | C→S | 鼠标/键盘输入事件 |
| STATS | 0x0050 | 双向 | 性能统计 |

## Packet Details

### HELLO (0x0001)
```
Payload:
┌──────────────┬──────────────┬──────────────┐
│ ClientWidth  │ ClientHeight │ RefreshRate  │
│ uint32       │ uint32       │ uint32       │
│ pixels       │ pixels       │ Hz           │
└──────────────┴──────────────┴──────────────┘
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
└──────────────┴──────────────┴──────────────┘
```

### FRAME_H264 (0x0030)
```
Payload:
┌──────────────┬──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ IsKeyFrame   │ NAL Data     │
│ uint64       │ uint64       │ uint8        │ bytes...     │
│              │ ms           │ 0/1          │              │
└──────────────┴──────────────┴──────────────┘
```

### FRAME_RAW (0x0031)
```
Payload:
┌──────────────┬──────────────┬──────────────┐
│ FrameIndex   │ Timestamp    │ BGRA Data    │
│ uint64       │ uint64       │ bytes...     │
│              │ ms           │              │
└──────────────┴──────────────┘──────────────┘
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

## Connection Flow

```
Windows (Client)                    Mac (Server)
     │                                  │
     │──── TCP Connect (port 9801) ────>│
     │──── HELLO ──────────────────────>│
     │<─── HELLO_ACK ──────────────────│
     │<─── DISPLAY_INFO ───────────────│
     │                                  │
     │  (video stream on port 9802)     │
     │<─── FRAME_H264 ─────────────────│
     │<─── FRAME_H264 ─────────────────│
     │<─── FRAME_H264 ─────────────────│
     │                                  │
     │──── INPUT_EVENT ────────────────>│
     │                                  │
```
