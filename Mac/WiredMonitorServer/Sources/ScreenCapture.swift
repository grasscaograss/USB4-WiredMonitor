import Foundation
import CoreGraphics
import CoreVideo
import IOSurface
import ScreenCaptureKit

class ScreenCapture {
    private let displayID: CGDirectDisplayID
    private let fps: Int
    private let scale: Double
    private var isRunning = false
    private var stream: SCStream?
    private var streamOutput: StreamOutput?
    private var displayStream: CGDisplayStream?
    private var fallbackTimer: DispatchSourceTimer?
    private var fallbackPixelBuffer: CVPixelBuffer?
    private var fallbackContext: CGContext?
    private var fallbackBaseAddress: UnsafeMutableRawPointer?
    private let fallbackColorSpace = CGColorSpaceCreateDeviceRGB()

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

        let captureMode = ProcessInfo.processInfo.environment["WIRED_MONITOR_CAPTURE"]?.lowercased()
        if captureMode == "image" || captureMode == "cgimage" {
            print("[捕获] 强制使用 CGDisplayCreateImage 主动抓屏模式")
            isRunning = false
            await startFallback()
            return
        }

        if captureMode == "cgstream" || ProcessInfo.processInfo.environment["WIRED_MONITOR_ACTIVE_CAPTURE"] == "1" {
            print("[捕获] 强制使用 CGDisplayStream 主动捕获模式")
            startDisplayStream()
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
        if let displayStream = displayStream {
            _ = displayStream.stop()
        }
        displayStream = nil
        stream = nil
        streamOutput = nil
        fallbackContext = nil
        fallbackPixelBuffer = nil
        fallbackBaseAddress = nil
        print("[捕获] 已停止")
    }

    private func startDisplayStream() {
        isRunning = true

        if let mode = CGDisplayCopyDisplayMode(displayID) {
            width = alignVideoDimension(Int(Double(mode.pixelWidth) * scale))
            height = alignVideoDimension(Int(Double(mode.pixelHeight) * scale))
        } else {
            width = alignVideoDimension(Int(Double(CGDisplayPixelsWide(displayID)) * scale))
            height = alignVideoDimension(Int(Double(CGDisplayPixelsHigh(displayID)) * scale))
        }

        let queue = DispatchQueue(label: "com.wiredmonitor.cgdisplaystream", qos: .userInteractive)
        let properties: [CFString: Any] = [
            CGDisplayStream.showCursor: true,
            CGDisplayStream.queueDepth: 1,
            CGDisplayStream.minimumFrameTime: 1.0 / Double(fps),
        ]

        let pixelFormat = Int32(bitPattern: kCVPixelFormatType_32BGRA)
        guard let stream = CGDisplayStream(
            dispatchQueueDisplay: displayID,
            outputWidth: width,
            outputHeight: height,
            pixelFormat: pixelFormat,
            properties: properties as CFDictionary,
            queue: queue,
            handler: { [weak self] status, displayTime, surface, update in
                self?.handleDisplayStreamFrame(status: status, displayTime: displayTime, surface: surface)
            })
        else {
            print("[捕获] CGDisplayStream 创建失败，回退到 CGDisplayCreateImage")
            isRunning = false
            Task { await self.startFallback() }
            return
        }

        let error = stream.start()
        guard error == .success else {
            print("[捕获] CGDisplayStream 启动失败: \(error.rawValue)，回退到 CGDisplayCreateImage")
            _ = stream.stop()
            isRunning = false
            Task { await self.startFallback() }
            return
        }

        displayStream = stream
        print("[捕获] CGDisplayStream 已启动 \(width)x\(height) @ \(fps)fps")
    }

    private func handleDisplayStreamFrame(status: CGDisplayStreamFrameStatus, displayTime: UInt64, surface: IOSurface?) {
        guard isRunning else { return }
        guard status == .frameComplete else { return }
        guard let surface else { return }

        var unmanagedPixelBuffer: Unmanaged<CVPixelBuffer>?
        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:],
        ]
        let result = CVPixelBufferCreateWithIOSurface(
            kCFAllocatorDefault,
            surface,
            attributes as CFDictionary,
            &unmanagedPixelBuffer)

        guard result == kCVReturnSuccess, let unmanagedPixelBuffer else {
            print("[捕获] IOSurface 转 CVPixelBuffer 失败: \(result)")
            return
        }

        let pixelBuffer = unmanagedPixelBuffer.takeRetainedValue()
        let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
        onFrame?(pixelBuffer, ts)
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
        prepareFallbackBuffers()

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

        if fallbackPixelBuffer == nil || fallbackContext == nil {
            prepareFallbackBuffers()
        }

        guard let buffer = fallbackPixelBuffer else { return }

        CVPixelBufferLockBaseAddress(buffer, [])
        guard let baseAddress = CVPixelBufferGetBaseAddress(buffer) else {
            CVPixelBufferUnlockBaseAddress(buffer, [])
            return
        }

        if fallbackContext == nil || fallbackBaseAddress != baseAddress {
            fallbackContext = makeFallbackContext(buffer: buffer, baseAddress: baseAddress)
            fallbackBaseAddress = baseAddress
        }

        guard let ctx = fallbackContext else {
            CVPixelBufferUnlockBaseAddress(buffer, [])
            return
        }

        ctx.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        CVPixelBufferUnlockBaseAddress(buffer, [])

        let ts = UInt64(CFAbsoluteTimeGetCurrent() * 1000)
        onFrame?(buffer, ts)
    }

    private func prepareFallbackBuffers() {
        var pb: CVPixelBuffer?
        let status = CVPixelBufferCreate(kCFAllocatorDefault, width, height,
                                          kCVPixelFormatType_32BGRA,
                                          [kCVPixelBufferCGImageCompatibilityKey: true,
                                           kCVPixelBufferCGBitmapContextCompatibilityKey: true,
                                           kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary] as CFDictionary,
                                          &pb)
        guard status == kCVReturnSuccess, let buffer = pb else { return }

        fallbackPixelBuffer = buffer
        fallbackContext = nil
        fallbackBaseAddress = nil
    }

    private func makeFallbackContext(buffer: CVPixelBuffer, baseAddress: UnsafeMutableRawPointer) -> CGContext? {
        CGContext(data: baseAddress,
                  width: width, height: height,
                  bitsPerComponent: 8,
                  bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
                  space: fallbackColorSpace,
                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue)
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
