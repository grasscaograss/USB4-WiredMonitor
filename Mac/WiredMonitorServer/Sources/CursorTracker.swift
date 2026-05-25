import CoreGraphics
import Foundation

struct CursorPosition {
    let x: Int
    let y: Int
    let visible: Bool
    let timestamp: UInt64
}

final class CursorTracker {
    private let displayID: CGDirectDisplayID
    private let videoWidth: Int
    private let videoHeight: Int
    private let fps: Int
    private var timer: DispatchSourceTimer?
    private var lastPosition: CursorPosition?

    var onCursor: ((CursorPosition) -> Void)?

    init(displayID: CGDirectDisplayID, videoWidth: Int, videoHeight: Int, fps: Int = 120) {
        self.displayID = displayID
        self.videoWidth = videoWidth
        self.videoHeight = videoHeight
        self.fps = max(30, min(fps, 240))
    }

    func start() {
        guard timer == nil else { return }

        let queue = DispatchQueue(label: "com.wiredmonitor.cursor", qos: .userInteractive)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: 1.0 / Double(fps), leeway: .milliseconds(1))
        timer.setEventHandler { [weak self] in
            self?.pollCursor()
        }
        timer.resume()
        self.timer = timer
        print("[鼠标] 独立 cursor 通道已启动 @ \(fps)Hz")
    }

    func stop() {
        timer?.cancel()
        timer = nil
        print("[鼠标] 独立 cursor 通道已停止")
    }

    private func pollCursor() {
        let bounds = CGDisplayBounds(displayID)
        guard bounds.width > 0, bounds.height > 0 else { return }
        guard let event = CGEvent(source: nil) else { return }

        let location = event.location
        let inside = bounds.contains(location)
        let x = inside
            ? clamp(Int((location.x - bounds.origin.x) / bounds.width * CGFloat(videoWidth)), min: 0, max: videoWidth - 1)
            : 0
        let y = inside
            ? clamp(Int((location.y - bounds.origin.y) / bounds.height * CGFloat(videoHeight)), min: 0, max: videoHeight - 1)
            : 0

        let position = CursorPosition(
            x: x,
            y: y,
            visible: inside,
            timestamp: UInt64(CFAbsoluteTimeGetCurrent() * 1000))

        if let lastPosition,
           lastPosition.x == position.x,
           lastPosition.y == position.y,
           lastPosition.visible == position.visible {
            return
        }

        lastPosition = position
        onCursor?(position)
    }

    private func clamp(_ value: Int, min lower: Int, max upper: Int) -> Int {
        Swift.max(lower, Swift.min(value, upper))
    }
}
