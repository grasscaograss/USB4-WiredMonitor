import Foundation
import CoreGraphics
import CoreVideo
import CoreMedia
import VideoToolbox

class H264Encoder {
    private var session: VTCompressionSession?
    private let width: Int32
    private let height: Int32
    private var frameIndex: UInt64 = 0
    private var spsPPS: Data?
    private let fps: Int

    // 缓存 pixel buffer 和 color space，避免每帧重建
    private var cachedPixelBuffer: CVPixelBuffer?
    private var cachedContext: CGContext?
    private let colorSpace = CGColorSpaceCreateDeviceRGB()
    private var needsKeyFrame = true
    private var encodeInputCount: UInt64 = 0
    private var encodeOutputCount: UInt64 = 0
    private var lastInputReportTime = Date()
    private let keyFrameInterval: UInt64
    private let bitRate: Int
    private let completeEveryFrame: Bool
    private let completeFrameInterval: UInt64
    private let quality: Double
    private let timestampLock = NSLock()
    private var frameTimestamps: [Int64: UInt64] = [:]

    var onNALUnit: ((Data, Bool, UInt64, UInt64) -> Void)?

    init(width: Int, height: Int, fps: Int = 60) {
        self.width = Int32(width)
        self.height = Int32(height)
        self.fps = fps
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_KEY_INTERVAL"],
           let parsed = UInt64(value),
           parsed > 0 {
            keyFrameInterval = parsed
        } else {
            keyFrameInterval = UInt64(max(fps * 4, 120))
        }

        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_BITRATE"],
           let parsed = Int(value),
           parsed > 0 {
            bitRate = parsed
        } else {
            bitRate = 60_000_000
        }

        let env = ProcessInfo.processInfo.environment
        if env["WIRED_MONITOR_ASYNC_ENCODER"] == "1" {
            completeEveryFrame = false
        } else if env["WIRED_MONITOR_SYNC_ENCODER"] == "1" {
            completeEveryFrame = true
        } else {
            completeEveryFrame = false
        }

        if let value = env["WIRED_MONITOR_FLUSH_INTERVAL"],
           let parsed = UInt64(value),
           parsed > 0,
           parsed <= 120 {
            completeFrameInterval = parsed
        } else {
            completeFrameInterval = 1
        }

        if let value = env["WIRED_MONITOR_QUALITY"],
           let parsed = Double(value),
           parsed > 0,
           parsed <= 1 {
            quality = parsed
        } else {
            quality = 0.88
        }
    }

    func start() -> Bool {
        var session: VTCompressionSession?

        // sourceImageBufferAttributes
        let sourceAttributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: Int(width),
            kCVPixelBufferHeightKey as String: Int(height),
            kCVPixelBufferIOSurfacePropertiesKey as String: [:],
        ]

        let status = VTCompressionSessionCreate(
            allocator: nil,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: sourceAttributes as CFDictionary,
            compressedDataAllocator: nil,
            outputCallback: { outputCallbackRefCon, _, status, _, sampleBuffer in
                let enc = Unmanaged<H264Encoder>.fromOpaque(outputCallbackRefCon!).takeUnretainedValue()
                guard status == noErr else {
                    print("[编码器] 输出回调失败: \(status)")
                    return
                }
                guard let sampleBuffer = sampleBuffer else {
                    print("[编码器] 输出回调 sampleBuffer 为 nil")
                    return
                }
                enc.handleEncodedFrame(sampleBuffer: sampleBuffer)
            },
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &session
        )

        guard status == noErr, let session = session else {
            print("[编码器] 创建失败: \(status)")
            return false
        }

        var err: OSStatus

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        if err != noErr { print("[编码器] 设置 RealTime 失败: \(err)") }

        let profile = Self.profileLevel()
        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: profile.value as AnyObject)
        if err != noErr { print("[编码器] 设置 ProfileLevel 失败: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: NSNumber(value: bitRate))
        if err != noErr { print("[编码器] 设置 BitRate 失败: \(err)") }

        // DataRateLimits: [bytes, seconds]
        let dataRateLimits = [
            NSNumber(value: (bitRate * 2) / 8),
            NSNumber(value: 1),
        ] as CFArray
        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_DataRateLimits, value: dataRateLimits)
        if err != noErr { print("[编码器] 设置 DataRateLimits 失败: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxFrameDelayCount, value: NSNumber(value: 0))
        if err != noErr { print("[编码器] 设置 MaxFrameDelay 失败: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        if err != noErr { print("[编码器] 设置 FrameReordering 失败: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: NSNumber(value: fps))
        if err != noErr { print("[编码器] 设置 ExpectedFrameRate 失败: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: NSNumber(value: keyFrameInterval))
        if err != noErr { print("[编码器] 设置 MaxKeyFrameInterval 失败: \(err)") }

        let keyFrameIntervalDuration = Double(keyFrameInterval) / Double(max(fps, 1))
        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, value: NSNumber(value: keyFrameIntervalDuration))
        if err != noErr { print("[编码器] 设置 MaxKeyFrameIntervalDuration 失败: \(err)") }

        err = VTSessionSetProperty(session, key: kVTCompressionPropertyKey_Quality, value: NSNumber(value: quality))
        if err != noErr { print("[编码器] 设置 Quality 失败: \(err)") }

        err = VTCompressionSessionPrepareToEncodeFrames(session)
        guard err == noErr else {
            print("[编码器] 准备失败: \(err)")
            return false
        }

        self.session = session
        print("[编码器] H.264 编码器已启动 (\(width)x\(height) @ \(fps)fps), 码率: \(Double(bitRate) / 1_000_000.0) Mbps, 关键帧间隔: \(keyFrameInterval), sync=\(completeEveryFrame), flushInterval=\(completeFrameInterval), quality=\(quality), profile=\(profile.name)")
        return true
    }

    private static func profileLevel() -> (name: String, value: CFString) {
        let profile = ProcessInfo.processInfo.environment["WIRED_MONITOR_H264_PROFILE"]?.lowercased()
        switch profile {
        case "baseline":
            return ("baseline", kVTProfileLevel_H264_Baseline_AutoLevel)
        case "main":
            return ("main", kVTProfileLevel_H264_Main_AutoLevel)
        default:
            return ("high", kVTProfileLevel_H264_High_AutoLevel)
        }
    }

    func encode(pixelBuffer: CVPixelBuffer, timestamp: UInt64) {
        guard let session = session else { return }

        encodeInputCount += 1
        let now = Date()
        if now.timeIntervalSince(lastInputReportTime) >= 1.0 {
            print("[编码器] 输入帧: \(encodeInputCount), 输出帧: \(encodeOutputCount)")
            lastInputReportTime = now
        }

        let presentationTime = CMTime(value: Int64(frameIndex), timescale: CMTimeScale(fps))
        frameIndex += 1
        timestampLock.lock()
        frameTimestamps[presentationTime.value] = timestamp
        timestampLock.unlock()

        let isKeyFrame = needsKeyFrame || frameIndex % keyFrameInterval == 0
        needsKeyFrame = false
        let frameProps: CFDictionary? = isKeyFrame
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
            : nil

        let status = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: pixelBuffer,
            presentationTimeStamp: presentationTime,
            duration: .invalid,
            frameProperties: frameProps,
            sourceFrameRefcon: nil,
            infoFlagsOut: nil
        )

        if status != noErr {
            print("[编码器] EncodeFrame 失败: \(status)")
            timestampLock.lock()
            frameTimestamps.removeValue(forKey: presentationTime.value)
            timestampLock.unlock()
        }

        // 低延迟模式下不让 VideoToolbox 内部积压输入帧；宁可降低 FPS，也不要输出旧画面。
        if completeEveryFrame || encodeInputCount <= 3 || isKeyFrame || frameIndex % completeFrameInterval == 0 {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: presentationTime)
        }
    }

    func stop() {
        if let session = session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        }
        session = nil
        timestampLock.lock()
        frameTimestamps.removeAll()
        timestampLock.unlock()
        print("[编码器] 已停止")
    }

    private func handleEncodedFrame(sampleBuffer: CMSampleBuffer) {
        guard sampleBuffer.numSamples > 0 else {
            print("[编码器] sampleBuffer 无样本")
            return
        }

        var isKeyFrame = isSampleKeyFrame(sampleBuffer)
        if encodeOutputCount == 0 {
            isKeyFrame = true
        }

        if isKeyFrame {
            spsPPS = extractSPSPPS(from: sampleBuffer)
            if spsPPS == nil {
                print("[编码器] 关键帧未取到 SPS/PPS")
            }
        }

        guard let sliceData = extractSliceData(from: sampleBuffer) else {
            print("[编码器] 未取到 slice data")
            return
        }

        var nalData = Data()
        if isKeyFrame, let spsPPS = spsPPS {
            nalData.append(spsPPS)
        }
        nalData.append(sliceData)

        let presentationTime = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let timestamp = takeFrameTimestamp(presentationTime: presentationTime)
        encodeOutputCount += 1
        if encodeOutputCount <= 3 || isKeyFrame {
            print("[编码器] 输出帧 #\(encodeOutputCount), key=\(isKeyFrame), bytes=\(nalData.count)")
        }
        onNALUnit?(nalData, isKeyFrame, frameIndex, timestamp)
    }

    private func takeFrameTimestamp(presentationTime: CMTime) -> UInt64 {
        timestampLock.lock()
        let timestamp = frameTimestamps.removeValue(forKey: presentationTime.value)
        timestampLock.unlock()

        return timestamp ?? UInt64(CFAbsoluteTimeGetCurrent() * 1000)
    }

    private func isSampleKeyFrame(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as NSArray?,
              attachments.count > 0,
              let dict = attachments[0] as? NSDictionary else {
            return needsKeyFrame
        }

        if let notSync = dict[kCMSampleAttachmentKey_NotSync] as? Bool {
            return !notSync
        }
        if let dependsOnOthers = dict[kCMSampleAttachmentKey_DependsOnOthers] as? Bool {
            return !dependsOnOthers
        }

        return needsKeyFrame
    }

    private func extractSPSPPS(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer) else { return nil }

        var result = Data()
        var paramCount: Int = 0

        // 获取参数集数量 (先调用 index 0 获取 count)
        let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            description,
            parameterSetIndex: 0,
            parameterSetPointerOut: nil,
            parameterSetSizeOut: nil,
            parameterSetCountOut: &paramCount,
            nalUnitHeaderLengthOut: nil
        )
        guard status == noErr else { return nil }

        for i in 0..<paramCount {
            var ptr: UnsafePointer<UInt8>?
            var size: Int = 0
            guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                description,
                parameterSetIndex: i,
                parameterSetPointerOut: &ptr,
                parameterSetSizeOut: &size,
                parameterSetCountOut: nil,
                nalUnitHeaderLengthOut: nil
            ) == noErr, let ptr = ptr else { continue }

            result.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            result.append(UnsafeBufferPointer(start: ptr, count: size))
        }

        return result.isEmpty ? nil : result
    }

    private func extractSliceData(from sampleBuffer: CMSampleBuffer) -> Data? {
        guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return nil }

        let length = CMBlockBufferGetDataLength(blockBuffer)
        var data = Data(count: length)

        data.withUnsafeMutableBytes { ptr in
            guard let base = ptr.baseAddress else { return }
            CMBlockBufferCopyDataBytes(blockBuffer, atOffset: 0, dataLength: length, destination: base)
        }

        // AVCC → Annex-B
        var result = Data()
        var offset = 0
        while offset + 4 <= data.count {
            let b0 = Int(data[offset])
            let b1 = Int(data[offset + 1])
            let b2 = Int(data[offset + 2])
            let b3 = Int(data[offset + 3])
            let nalLen = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3
            offset += 4
            guard offset + nalLen <= data.count else { break }
            result.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            result.append(data[offset..<(offset + nalLen)])
            offset += nalLen
        }

        return result.isEmpty ? nil : result
    }

    private func createPixelBuffer(from image: CGImage) -> CVPixelBuffer? {
        let pxWidth = image.width
        let pxHeight = image.height

        // 复用缓存的 pixel buffer
        if cachedPixelBuffer == nil {
            var pb: CVPixelBuffer?
            let status = CVPixelBufferCreate(
                kCFAllocatorDefault,
                Int(width),
                Int(height),
                kCVPixelFormatType_32BGRA,
                [
                    kCVPixelBufferCGImageCompatibilityKey: true,
                    kCVPixelBufferCGBitmapContextCompatibilityKey: true,
                    kCVPixelBufferIOSurfacePropertiesKey: [:] as CFDictionary,
                ] as CFDictionary,
                &pb
            )
            guard status == kCVReturnSuccess, let buffer = pb else { return nil }
            cachedPixelBuffer = buffer
        }

        guard let buffer = cachedPixelBuffer else { return nil }

        CVPixelBufferLockBaseAddress(buffer, [])
        defer { CVPixelBufferUnlockBaseAddress(buffer, []) }

        guard let context = CGContext(
            data: CVPixelBufferGetBaseAddress(buffer),
            width: Int(width),
            height: Int(height),
            bitsPerComponent: 8,
            bytesPerRow: CVPixelBufferGetBytesPerRow(buffer),
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
        ) else { return nil }

        context.draw(image, in: CGRect(x: 0, y: 0, width: Int(width), height: Int(height)))
        return buffer
    }
}
