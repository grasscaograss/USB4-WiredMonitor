import Foundation
import CoreGraphics
import CoreVideo

@main
struct WiredMonitorServer {
    private static var h264PumpTimer: DispatchSourceTimer?
    private static var rawPumpTimer: DispatchSourceTimer?

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

        let streamFps = streamFPS()
        let capture = ScreenCapture(fps: streamFps)
        let (width, height) = mainDisplayResolution()
        guard width > 0, height > 0 else {
            print("[主] 无法获取屏幕分辨率")
            return
        }

        print("[主] 屏幕分辨率: \(width)x\(height)")

        let encoder = H264Encoder(width: width, height: height, fps: streamFps)
        let forceRaw = ProcessInfo.processInfo.environment["WIRED_MONITOR_RAW"] == "1"
        if !forceRaw && encoder.start() {
            print("[主] H.264 编码模式")
            startH264Mode(capture: capture, encoder: encoder, server: server, width: width, height: height, fps: streamFps)
        } else {
            print(forceRaw ? "[主] 已强制使用 RAW 模式" : "[主] H.264 编码器启动失败，使用 RAW 模式")
            startRawMode(capture: capture, server: server, width: width, height: height, fps: streamFps)
        }

        print("[主] 服务已启动，等待 Windows 客户端连接...")
        print("[主] 视频流端口: \(VideoPort)")
        print()

        let captureStartLock = NSLock()
        var captureStarted = false
        server.onFirstClientConnected = {
            captureStartLock.lock()
            let shouldStart = !captureStarted
            if shouldStart {
                captureStarted = true
            }
            captureStartLock.unlock()

            guard shouldStart else { return }

            Task {
                print("[主] 客户端已连接，启动屏幕捕获...")
                await capture.start()
            }
        }
        if server.clientCount > 0 {
            server.onFirstClientConnected?()
        }

        signalSource.setEventHandler {
            print("\n[主] 正在关闭...")
            capture.stop()
            encoder.stop()
            server.stop()
            exit(0)
        }
        signalSource.resume()

        // 保持运行
        try? await Task.sleep(for: .seconds(1_000_000))
    }

    static func mainDisplayResolution() -> (width: Int, height: Int) {
        let scale: Double
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_SCALE"],
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            scale = parsed
        } else {
            scale = 1
        }

        let displayID = CGMainDisplayID()
        if let mode = CGDisplayCopyDisplayMode(displayID) {
            return (
                alignVideoDimension(Int(Double(mode.pixelWidth) * scale)),
                alignVideoDimension(Int(Double(mode.pixelHeight) * scale))
            )
        }

        return (
            alignVideoDimension(Int(Double(CGDisplayPixelsWide(displayID)) * scale)),
            alignVideoDimension(Int(Double(CGDisplayPixelsHigh(displayID)) * scale))
        )
    }

    static func streamFPS() -> Int {
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_FPS"],
           let parsed = Int(value),
           parsed > 0,
           parsed <= 120 {
            return parsed
        }

        return 30
    }

    static func startH264Mode(capture: ScreenCapture, encoder: H264Encoder, server: FrameServer, width: Int, height: Int, fps: Int) {
        var frameCount: UInt64 = 0
        var inputFrameCount: UInt64 = 0
        var lastReportTime = Date()
        var lastReportFrame: UInt64 = 0
        var lastInputReportTime = Date()
        var lastInputReportFrame: UInt64 = 0
        var lastInputHash: UInt64 = 0

        encoder.onNALUnit = { nalData, isKeyFrame, _, _ in
            frameCount += 1

            // 帧数据: [8字节 frameIndex] [8字节 timestamp] [1字节 isKeyFrame] [NAL data]
            var payload = Data()
            var idx = frameCount.littleEndian
            var ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000).littleEndian
            var kf: UInt8 = isKeyFrame ? 1 : 0
            var w = UInt32(width).littleEndian
            var h = UInt32(height).littleEndian

            payload.append(Data(bytes: &idx, count: 8))
            payload.append(Data(bytes: &ts, count: 8))
            payload.append(Data(bytes: &kf, count: 1))
            payload.append(Data(bytes: &w, count: 4))
            payload.append(Data(bytes: &h, count: 4))
            payload.append(nalData)

            server.sendFrame(data: payload, packetType: .frameH264, cacheForNewClients: isKeyFrame)

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
            encoder.encode(pixelBuffer: pixelBuffer, timestamp: timestamp)

            let now = Date()
            if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
                let elapsed = now.timeIntervalSince(lastInputReportTime)
                let fps = Double(inputFrameCount - lastInputReportFrame) / elapsed
                let hash = pixelBufferSampleHash(pixelBuffer)
                let changed = hash != lastInputHash
                print("[捕获统计] 输入 FPS: \(String(format: "%.1f", fps)), hash: \(String(hash, radix: 16)), changed: \(changed), 客户端: \(server.clientCount)")
                lastInputHash = hash
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
                let hash = pixelBufferSampleHash(pixelBuffer)
                let changed = hash != lastInputHash
                print("[捕获统计-RAW] 输入 FPS: \(String(format: "%.1f", fps)), hash: \(String(hash, radix: 16)), changed: \(changed), 客户端: \(server.clientCount)")
                lastInputHash = hash
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
