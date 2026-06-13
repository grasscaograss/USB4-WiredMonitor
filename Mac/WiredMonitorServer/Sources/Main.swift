import Foundation
import CoreGraphics
import CoreVideo

private final class StreamRuntime {
    let capture: ScreenCapture
    let encoder: H264Encoder?
    let cursorTracker: CursorTracker?
    let virtualDisplay: VirtualDisplay?

    init(capture: ScreenCapture, encoder: H264Encoder?, cursorTracker: CursorTracker?, virtualDisplay: VirtualDisplay?) {
        self.capture = capture
        self.encoder = encoder
        self.cursorTracker = cursorTracker
        self.virtualDisplay = virtualDisplay
    }

    func stop() {
        cursorTracker?.stop()
        capture.stop()
        encoder?.stop()
    }
}

@main
struct WiredMonitorServer {
    private static var h264PumpTimer: DispatchSourceTimer?
    private static var rawPumpTimer: DispatchSourceTimer?
    private static let runtimeQueue = DispatchQueue(label: "com.wiredmonitor.runtime")
    private static var activeRuntime: StreamRuntime?

    static func main() async {
        print("╔══════════════════════════════════════════╗")
        print("║   Wired Monitor Server - Mac 扩展屏      ║")
        print("║   通过 Thunderbolt/USB4 传输屏幕画面      ║")
        print("╚══════════════════════════════════════════╝")
        print()

        let signalSource = DispatchSource.makeSignalSource(signal: SIGINT, queue: .main)
        signal(SIGINT, SIG_IGN)

        let server = FrameServer(port: VideoPort)
        guard server.start() else {
            print("[主] 服务端启动失败")
            return
        }

        print("[主] 服务已启动，等待 Windows 客户端连接后创建 Mac 虚拟副屏...")
        print("[主] 视频流端口: \(VideoPort)")
        print()

        server.onFirstClientConnected = { clientInfo in
            Task {
                guard !hasActiveRuntime() else { return }

                print("[主] 客户端已连接，准备虚拟显示与屏幕捕获...")
                guard let runtime = await configureAndStartStreaming(server: server, clientInfo: clientInfo) else {
                    return
                }

                if !installRuntimeIfNeeded(runtime) {
                    runtime.stop()
                }
            }
        }

        signalSource.setEventHandler {
            print("\n[主] 正在关闭...")
            let runtime = takeActiveRuntime()
            runtime?.stop()
            server.stop()
            exit(0)
        }
        signalSource.resume()

        // 保持运行
        try? await Task.sleep(for: .seconds(1_000_000))
    }

    private static func hasActiveRuntime() -> Bool {
        runtimeQueue.sync {
            activeRuntime != nil
        }
    }

    private static func installRuntimeIfNeeded(_ runtime: StreamRuntime) -> Bool {
        runtimeQueue.sync {
            guard activeRuntime == nil else { return false }
            activeRuntime = runtime
            return true
        }
    }

    private static func takeActiveRuntime() -> StreamRuntime? {
        runtimeQueue.sync {
            let runtime = activeRuntime
            activeRuntime = nil
            return runtime
        }
    }

    private static func configureAndStartStreaming(server: FrameServer, clientInfo: ClientDisplayInfo?) async -> StreamRuntime? {
        let streamFps = streamFPS(clientInfo: clientInfo)
        let virtualConfig = VirtualDisplayConfiguration.load(clientInfo: clientInfo, fps: streamFps)

        let virtualDisplay: VirtualDisplay?
        let displayID: CGDirectDisplayID
        let fallbackResolution: (width: Int, height: Int)?

        if virtualConfig.enabled {
            guard let createdDisplay = VirtualDisplay.create(configuration: virtualConfig) else {
                print("[主] 虚拟显示创建失败；如需临时回到主屏镜像，设置 WIRED_MONITOR_MIRROR_MAIN=1")
                return nil
            }

            virtualDisplay = createdDisplay
            displayID = createdDisplay.displayID
            fallbackResolution = (virtualConfig.width, virtualConfig.height)
        } else {
            virtualDisplay = nil
            displayID = CGMainDisplayID()
            fallbackResolution = nil
            print("[主] 已使用主屏镜像模式")
        }

        let (width, height) = displayResolution(displayID: displayID, fallback: fallbackResolution)
        guard width > 0, height > 0 else {
            print("[主] 无法获取显示器分辨率")
            return nil
        }

        print("[主] 捕获显示器: \(displayID), 分辨率: \(width)x\(height), FPS: \(streamFps)")

        let capture = ScreenCapture(displayID: displayID, fps: streamFps)
        let encoder = H264Encoder(width: width, height: height, fps: streamFps)
        let cursorTracker = makeCursorTracker(displayID: displayID, width: width, height: height, server: server)
        let forceRaw = ProcessInfo.processInfo.environment["WIRED_MONITOR_RAW"] == "1"
        let runtime: StreamRuntime

        if !forceRaw && encoder.start() {
            print("[主] \(encoder.codecName) 编码模式")
            startH264Mode(capture: capture, encoder: encoder, server: server, width: width, height: height, fps: streamFps)
            runtime = StreamRuntime(capture: capture, encoder: encoder, cursorTracker: cursorTracker, virtualDisplay: virtualDisplay)
        } else {
            if !forceRaw && !encoder.usesDefaultCodec && ProcessInfo.processInfo.environment["WIRED_MONITOR_ALLOW_RAW_FALLBACK"] != "1" {
                print("[主] \(encoder.codecName) 编码器启动失败，已停止；如需回退 RAW，设置 WIRED_MONITOR_ALLOW_RAW_FALLBACK=1")
                return nil
            }
            print(forceRaw ? "[主] 已强制使用 RAW 模式" : "[主] H.264 编码器启动失败，使用 RAW 模式")
            startRawMode(capture: capture, server: server, width: width, height: height, fps: streamFps)
            runtime = StreamRuntime(capture: capture, encoder: nil, cursorTracker: cursorTracker, virtualDisplay: virtualDisplay)
        }

        print("[主] 启动屏幕捕获...")
        await capture.start()
        cursorTracker?.start()
        return runtime
    }

    private static func makeCursorTracker(displayID: CGDirectDisplayID, width: Int, height: Int, server: FrameServer) -> CursorTracker? {
        if captureIncludesCursor() {
            print("[鼠标] 已使用视频帧内 cursor")
            return nil
        }

        guard envBool("WIRED_MONITOR_SEPARATE_CURSOR", defaultValue: false) else {
            print("[鼠标] 独立 cursor 通道未启用")
            return nil
        }

        let cursorFps: Int
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_CURSOR_FPS"],
           let parsed = Int(value),
           parsed >= 30,
           parsed <= 240 {
            cursorFps = parsed
        } else {
            cursorFps = 120
        }

        let tracker = CursorTracker(displayID: displayID, videoWidth: width, videoHeight: height, fps: cursorFps)
        tracker.onCursor = { position in
            var payload = Data(capacity: 17)
            var ts = position.timestamp.littleEndian
            var x = UInt32(position.x).littleEndian
            var y = UInt32(position.y).littleEndian
            var visible: UInt8 = position.visible ? 1 : 0

            payload.append(Data(bytes: &ts, count: 8))
            payload.append(Data(bytes: &x, count: 4))
            payload.append(Data(bytes: &y, count: 4))
            payload.append(Data(bytes: &visible, count: 1))
            server.sendRealtime(data: payload, packetType: .cursorPosition)
        }

        return tracker
    }

    private static func captureIncludesCursor() -> Bool {
        envBool("WIRED_MONITOR_CAPTURE_CURSOR", defaultValue: true)
    }

    private static func envBool(_ key: String, defaultValue: Bool) -> Bool {
        guard let value = ProcessInfo.processInfo.environment[key]?.lowercased() else {
            return defaultValue
        }

        switch value {
        case "1", "true", "yes", "on":
            return true
        case "0", "false", "no", "off":
            return false
        default:
            return defaultValue
        }
    }

    static func displayResolution(displayID: CGDirectDisplayID, fallback: (width: Int, height: Int)? = nil) -> (width: Int, height: Int) {
        let scale = captureScale()

        if let mode = CGDisplayCopyDisplayMode(displayID) {
            return (
                alignVideoDimension(Int(Double(mode.pixelWidth) * scale)),
                alignVideoDimension(Int(Double(mode.pixelHeight) * scale))
            )
        }

        let displayWidth = CGDisplayPixelsWide(displayID)
        let displayHeight = CGDisplayPixelsHigh(displayID)
        if displayWidth > 0, displayHeight > 0 {
            return (
                alignVideoDimension(Int(Double(displayWidth) * scale)),
                alignVideoDimension(Int(Double(displayHeight) * scale))
            )
        }

        if let fallback {
            return (
                alignVideoDimension(Int(Double(fallback.width) * scale)),
                alignVideoDimension(Int(Double(fallback.height) * scale))
            )
        }

        return (0, 0)
    }

    static func captureScale() -> Double {
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_SCALE"],
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            return parsed
        }

        return 1
    }

    static func streamFPS(clientInfo: ClientDisplayInfo? = nil) -> Int {
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_FPS"],
           let parsed = Int(value),
           parsed > 0,
           parsed <= 120 {
            return parsed
        }

        if let refreshRate = clientInfo?.refreshRate,
           refreshRate >= 24 {
            return min(refreshRate, 60)
        }

        return 60
    }

    static func startH264Mode(capture: ScreenCapture, encoder: H264Encoder, server: FrameServer, width: Int, height: Int, fps: Int) {
        var frameCount: UInt64 = 0
        var inputFrameCount: UInt64 = 0
        var lastReportTime = Date()
        var lastReportFrame: UInt64 = 0
        var lastInputReportTime = Date()
        var lastInputReportFrame: UInt64 = 0
        var lastInputHash: UInt64 = 0
        let pendingFrameLock = NSLock()
        let encoderQueue = DispatchQueue(label: "com.wiredmonitor.h264-encoder", qos: .userInteractive)
        var latestPixelBuffer: CVPixelBuffer?
        var latestTimestamp: UInt64 = 0
        var encoderScheduled = false
        var skippedForBackpressure: UInt64 = 0
        var lastBackpressureReportTime = Date()
        let hashDiagnosticsEnabled = envBool("WIRED_MONITOR_DIAG_HASH", defaultValue: false)

        func scheduleEncoderIfNeeded() {
            pendingFrameLock.lock()
            if encoderScheduled {
                pendingFrameLock.unlock()
                return
            }
            encoderScheduled = true
            pendingFrameLock.unlock()

            encoderQueue.async {
                while true {
                    pendingFrameLock.lock()
                    guard let pixelBuffer = latestPixelBuffer else {
                        encoderScheduled = false
                        pendingFrameLock.unlock()
                        return
                    }
                    let timestamp = latestTimestamp
                    latestPixelBuffer = nil
                    pendingFrameLock.unlock()

                    guard server.clientCount > 0 else { continue }
                    if server.isFrameBackpressured {
                        skippedForBackpressure += 1
                        let now = Date()
                        if now.timeIntervalSince(lastBackpressureReportTime) >= 1.0 {
                            print("[编码器] 网络发送未完成，跳过编码输入: \(skippedForBackpressure)")
                            skippedForBackpressure = 0
                            lastBackpressureReportTime = now
                        }
                        continue
                    }
                    encoder.encode(pixelBuffer: pixelBuffer, timestamp: timestamp)
                }
            }
        }

        encoder.onNALUnit = { nalData, isKeyFrame, _, captureTimestamp in
            frameCount += 1

            // 帧数据: [8字节 frameIndex] [8字节 timestamp] [1字节 isKeyFrame] [4字节 width] [4字节 height] [NAL data]
            var payload = Data()
            var idx = frameCount.littleEndian
            var ts = captureTimestamp.littleEndian
            var kf: UInt8 = isKeyFrame ? 1 : 0
            var w = UInt32(width).littleEndian
            var h = UInt32(height).littleEndian

            payload.append(Data(bytes: &idx, count: 8))
            payload.append(Data(bytes: &ts, count: 8))
            payload.append(Data(bytes: &kf, count: 1))
            payload.append(Data(bytes: &w, count: 4))
            payload.append(Data(bytes: &h, count: 4))
            payload.append(nalData)

            server.sendFrame(data: payload, packetType: encoder.packetType, cacheForNewClients: isKeyFrame)

            let now = Date()
            if now.timeIntervalSince(lastReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastReportTime)
                let fps = Double(frameCount - lastReportFrame) / elapsed
                let sizeKB = Double(nalData.count) / 1024.0
                print("[统计] FPS: \(String(format: "%.1f", fps)), 帧大小: \(String(format: "%.1f", sizeKB)) KB, 关键帧: \(isKeyFrame), 客户端: \(server.clientCount)")
                lastReportTime = now
                lastReportFrame = frameCount
            }
        }

        capture.onFrame = { pixelBuffer, _ in
            guard server.clientCount > 0 else { return }

            inputFrameCount += 1
            let timestamp = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
            pendingFrameLock.lock()
            latestPixelBuffer = pixelBuffer
            latestTimestamp = timestamp
            pendingFrameLock.unlock()
            scheduleEncoderIfNeeded()

            let now = Date()
            if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastInputReportTime)
                let fps = Double(inputFrameCount - lastInputReportFrame) / elapsed
                if hashDiagnosticsEnabled {
                    let hash = pixelBufferSampleHash(pixelBuffer)
                    let changed = hash != lastInputHash
                    print("[捕获统计] 输入 FPS: \(String(format: "%.1f", fps)), hash: \(String(hash, radix: 16)), changed: \(changed), 客户端: \(server.clientCount)")
                    lastInputHash = hash
                } else {
                    print("[捕获统计] 输入 FPS: \(String(format: "%.1f", fps)), 客户端: \(server.clientCount)")
                }
                lastInputReportTime = now
                lastInputReportFrame = inputFrameCount
            }
        }
    }

    static func startRawMode(capture: ScreenCapture, server: FrameServer, width: Int, height: Int, fps: Int) {
        var frameCount: UInt64 = 0
        var lastReportTime = Date()
        var lastReportFrame: UInt64 = 0
        var inputFrameCount: UInt64 = 0
        var lastInputReportTime = Date()
        var lastInputReportFrame: UInt64 = 0
        var lastInputHash: UInt64 = 0
        let hashDiagnosticsEnabled = envBool("WIRED_MONITOR_DIAG_HASH", defaultValue: false)
        let latestFrameLock = NSLock()
        var latestPixelBuffer: CVPixelBuffer?

        capture.onFrame = { pixelBuffer, _ in
            inputFrameCount += 1
            latestFrameLock.lock()
            latestPixelBuffer = pixelBuffer
            latestFrameLock.unlock()

            let now = Date()
            if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastInputReportTime)
                let fps = Double(inputFrameCount - lastInputReportFrame) / elapsed
                if hashDiagnosticsEnabled {
                    let hash = pixelBufferSampleHash(pixelBuffer)
                    let changed = hash != lastInputHash
                    print("[捕获统计-RAW] 输入 FPS: \(String(format: "%.1f", fps)), hash: \(String(hash, radix: 16)), changed: \(changed), 客户端: \(server.clientCount)")
                    lastInputHash = hash
                } else {
                    print("[捕获统计-RAW] 输入 FPS: \(String(format: "%.1f", fps)), 客户端: \(server.clientCount)")
                }
                lastInputReportTime = now
                lastInputReportFrame = inputFrameCount
            }
        }

        let pumpQueue = DispatchQueue(label: "com.wiredmonitor.raw-pump", qos: .userInteractive)
        let pumpTimer = DispatchSource.makeTimerSource(queue: pumpQueue)
        pumpTimer.schedule(deadline: .now(), repeating: 1.0 / Double(fps), leeway: .milliseconds(2))
        pumpTimer.setEventHandler {
            latestFrameLock.lock()
            let pixelBuffer = latestPixelBuffer
            latestFrameLock.unlock()

            guard server.clientCount > 0, let pixelBuffer else { return }

            frameCount += 1

            CVPixelBufferLockBaseAddress(pixelBuffer, [])
            defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }

            guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
            let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
            let dataSize = bytesPerRow * height
            let data = Data(bytes: baseAddress, count: dataSize)

            var payload = Data()
            var idx = frameCount.littleEndian
            var ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000).littleEndian
            var w = UInt32(width).littleEndian
            var h = UInt32(height).littleEndian
            var stride = UInt32(bytesPerRow).littleEndian

            payload.append(Data(bytes: &idx, count: 8))
            payload.append(Data(bytes: &ts, count: 8))
            payload.append(Data(bytes: &w, count: 4))
            payload.append(Data(bytes: &h, count: 4))
            payload.append(Data(bytes: &stride, count: 4))
            payload.append(data)

            server.sendFrame(data: payload, packetType: .frameRaw)

            let now = Date()
            if now.timeIntervalSince(lastReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastReportTime)
                let fps = Double(frameCount - lastReportFrame) / elapsed
                let sizeMB = Double(data.count) / (1024.0 * 1024.0)
                print("[统计-RAW] FPS: \(String(format: "%.1f", fps)), 帧大小: \(String(format: "%.1f", sizeMB)) MB, 客户端: \(server.clientCount)")
                lastReportTime = now
                lastReportFrame = frameCount
            }
        }
        rawPumpTimer = pumpTimer
        pumpTimer.resume()
    }

    static func pixelBufferSampleHash(_ pixelBuffer: CVPixelBuffer) -> UInt64 {
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

        guard let baseAddress = CVPixelBufferGetBaseAddress(pixelBuffer) else { return 0 }

        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let bytes = baseAddress.assumingMemoryBound(to: UInt8.self)

        var hash: UInt64 = 1469598103934665603
        let sampleRows = 8
        let sampleCols = 8

        for rowIndex in 0..<sampleRows {
            let y = min(height - 1, rowIndex * max(1, height / sampleRows))
            for colIndex in 0..<sampleCols {
                let x = min(width - 1, colIndex * max(1, width / sampleCols))
                let offset = y * bytesPerRow + x * 4
                for i in 0..<4 {
                    hash ^= UInt64(bytes[offset + i])
                    hash &*= 1099511628211
                }
            }
        }

        return hash
    }
}
