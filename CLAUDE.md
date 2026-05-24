# Wired Monitor

通过 USB4/Thunderbolt 连接 Mac 和 Windows，将 Windows 作为 Mac 的扩展屏。

## 架构

- **Mac 端 (Server)**: Swift, CGDisplayStream + VideoToolbox H.264 编码, TCP 发送
- **Windows 端 (Client)**: C#/.NET 8 WPF, TCP 接收 + FFmpeg H.264 解码 + WriteableBitmap 渲染
- **通信**: TCP/IP over Thunderbolt Networking (端口 9802 视频)

## 目录结构

```
Protocol/         - 通信协议文档
Mac/WiredMonitorServer/  - Mac Swift 端 (SPM 项目)
Windows/WiredMonitorClient/ - Windows .NET 8 WPF 端
NetworkTest/      - 网络带宽/延迟测试工具 (.NET 8 控制台)
```

## 使用方法

### 网络测试
```bash
# Windows 端 - 先发现 Thunderbolt 网络接口
dotnet run --project NetworkTest -- discovery

# Mac 端 - 启动测试服务端
# Windows 端 - 测试吞吐量
dotnet run --project NetworkTest -- client <Mac的IP>

# Windows 端 - 测试延迟
dotnet run --project NetworkTest -- ping <Mac的IP>
```

### 启动扩展屏
```bash
# Mac 端
cd Mac/WiredMonitorServer && swift run

# Windows 端
cd Windows/WiredMonitorClient && dotnet run
```

## Thunderbolt 网络配置

连接 USB4/TB 线缆后，Mac 和 Windows 会自动创建网络接口：
- Mac: 系统设置 → 网络 → Thunderbolt Bridge (通常分配 169.254.x.x)
- Windows: 设置 → 网络和 Internet → 查看以太网适配器 IP

## Windows 端依赖

FFmpeg native 库需要放在 `Windows/WiredMonitorClient/bin/<config>/net8.0-windows/ffmpeg/` 目录下。
下载地址: https://github.com/BtbN/FFmpeg-Builds/releases (ffmpeg-master-latest-win64-gpl-shared)

## 当前状态

技术验证阶段 (MVP):
- [x] 项目结构和协议定义
- [x] 网络带宽/延迟测试工具
- [x] Mac 端屏幕捕获 (CGDisplayStream) + VideoToolbox H.264 编码 + TCP 发送
- [x] Windows 端 TCP 接收 + WriteableBitmap 渲染 (RAW 模式)
- [x] Windows 端 FFmpeg H.264 解码器集成
- [x] 性能优化 (SPS/PPS 缓存, 零拷贝渲染, 编码参数调优)
- [ ] 输入事件转发 (鼠标/键盘)
- [ ] 虚拟显示器驱动 (Mac IOKit)
- [ ] 音频传输
