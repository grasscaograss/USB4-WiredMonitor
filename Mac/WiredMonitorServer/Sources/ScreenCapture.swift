import Foundation
import CoreGraphics
import CoreVideo
import ScreenCaptureKit

class ScreenCapture {
    private let displayID: CGDirectDisplayID
    private let fps: Int
    private let scale: Double
    private var isRunning = false
    private var stream: SCStream?
    private var streamOutput: StreamOutput?
    private var fallbackTimer: DispatchSourceTimer?

    private(set) var width: Int = 0
    private(set) var height: Int = 0

    var onFrame: ((CVPixelBuffer, UInt64) -> Void)?

    init(displayID: CGDirectDisplayID = CGMainDisplayID(), fps: Int = 60) {
        self.displayID = displayID
        self.fps = fps
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_SCALE"],
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            scale = parsed
        } else {
            scale = 1
        }
    }

    var resolution: (width: Int, height: Int) {
        (width, height)
    }

    func start() async {
        guard !isRunning else { return }
        isRunning = true

        if ProcessInfo.processInfo.environment["WIRED_MONITOR_ACTIVE_CAPTURE"] == "1" {
            print("[捕获] 强制使用 CGDisplay 主动抓屏模式")
            isRunning = false
            await startFallback()
            return
        }

        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)

            guard let display = content.displays.first(where: { $0.displayID == displayID }) ?? content.displays.first else {
                print("[捕获] 找不到显示器")
                isRunning = false
                return
            }

            // 使用原生像素分辨率 (Retina)
            if let mode = CGDisplayCopyDisplayMode(display.displayID) {
                width = alignVideoDimension(Int(Double(mode.pixelWidth) * scale))
                height = alignVideoDimension(Int(Double(mode.pixelHeight) * scale))
            } else {
                width = alignVideoDimension(Int(Double(display.width) * scale))
                height = alignVideoDimension(Int(Double(display.height) * scale))
            }

            let filter = SCContentFilter(display: display, excludingWindows: [])

            let config = SCStreamConfiguration()
            config.width = width
            config.height = height
            config.pixelFormat = kCVPixelFormatType_32BGRA
            config.minimumFrameInterval = CMTime(value: 1, timescale: Int32(fps))
            config.queueDepth = 1
            config.showsCursor = true
            config.capturesAudio = false

            let output = StreamOutput(capture: self)
            streamOutput = output

            stream = SCStream(filter: filter, configuration: config, delegate: nil)
            try stream!.addStreamOutput(output, type: .screen, sampleHandlerQueue: DispatchQueue(label: "com.wiredmonitor.sck", qos: .userInteractive))

            try await stream!.startCapture()

            print("[捕获] ScreenCaptureKit 已启动 \(width)x\(height) @ \(fps)fps")
        } catch {
            print("[捕获] ScreenCaptureKit 启动失败: \(error)")
            print("[捕获] 回退到 CGDisplayCreateImage")
            isRunning = false
            await startFallback()
        }
    }

    func stop() {
        isRunning = false
        if let stream = stream {
            Task { try? await stream.stopCapture() }
        }
        fallbackTimer?.cancel()
        fallbackTimer = nil
        stream = nil
        streamOutput = nil
        print("[捕获] 已停止")
    }

    // Fallback: 用 CGDisplayCreateImage
    private func startFallback() async {
        isRunning = true

        if let mode = CGDisplayCopyDisplayMode(displayID) {
            width = alignVideoDimension(Int(Double(mode.pixelWidth) * scale))
            height = alignVideoDimension(Int(Double(mode.pixelHeight) * scale))
        } else {
            width = alignVideoDimension(Int(Double(CGDisplayPixelsWide(displayID)) * scale))
            height = alignVideoDimension(Int(Double(CGDisplayPixelsHigh(displayID)) * scale))
        }

        print("[捕获] CGDisplay 回退模式 \(width)x\(height)")

        let interval = 1.0 / Double(fps)
        let queue = DispatchQueue(label: "com.wiredmonitor.capture", qos: .userInteractive)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: interval, leeway: .milliseconds(1))
        timer.setEventHandler { [weak self] in
            self?.captureFallbackFrame()
        }
        timer.resume()
        fallbackTimer = timer
    }

    private func captureFallbackFrame() {
        guard isRunning else { return }

        let rect = CGRect(x: 0, y: 0, width: CGFloat(CGDisplayPixelsWide(displayID)),
                          height: CGFloat(CGDisplayPixelsHigh(displayID)))
        guard let image = CGDisplayCreateImage(displayID, rect: rect) else { return }

        // CGImage → CVPixelBuffer
        var pb: CVPixelBuffer?
        let status = CVPixelBufferCreate(kCFAllocatorDefault, width, height,
                                          kCVPixelFormatType_32BGRA,
                                          [kCVPixelBufferCGImageCompatibilityKey: true,
                                           kCVPixelBufferCGBitmapContextCompatibilityKey: true] as CFDictionary,
                                          &pb)
        guard status == kCVReturnSuccess, let buffer = pb else { return }

        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }

        guard let ctx = CGContext(data: CVPixelBufferGetBaseAddress(buffer),
                                   width: width, height: height,
                                   bitsPerComponent: 8,
                                   bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
                                   space: CGColorSpaceCreateDeviceRGB(),
                                   bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
        else { return }

        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
        onFrame?(buffer, ts)
    }

    // ScreenCaptureKit output handler
    class StreamOutput: NSObject, SCStreamOutput {
        weak var capture: ScreenCapture?

        init(capture: ScreenCapture) {
            self.capture = capture
        }

        func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
            guard outputType == .screen else { return }
            guard let pixelBuffer = sampleBuffer.imageBuffer else {
                print("[捕获] imageBuffer 为 nil")
                return
            }

            let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
            print("[捕获] 收到帧 \(ts)")
            capture?.onFrame?(pixelBuffer, ts)
        }
    }
}
