import CoreGraphics
import Foundation
import VirtualDisplayBridge

struct VirtualDisplayConfiguration {
    let enabled: Bool
    let width: Int
    let height: Int
    let logicalWidth: Int
    let logicalHeight: Int
    let refreshRate: Int
    let pixelsPerInch: Int
    let hiDPI: Bool
    let backingScale: Int
    let name: String

    static func load(clientInfo: ClientDisplayInfo?, fps: Int) -> VirtualDisplayConfiguration {
        let enabled = ProcessInfo.processInfo.environment["WIRED_MONITOR_MIRROR_MAIN"] != "1" &&
            ProcessInfo.processInfo.environment["WIRED_MONITOR_VIRTUAL_DISPLAY"] != "0"

        let clientWidth = validDimension(clientInfo?.width) ? clientInfo!.width : 1920
        let clientHeight = validDimension(clientInfo?.height) ? clientInfo!.height : 1080
        let clientRefresh = validRefreshRate(clientInfo?.refreshRate) ? clientInfo!.refreshRate : fps

        let width = envInt("WIRED_MONITOR_VIRTUAL_WIDTH", min: 640, max: 16_384) ?? clientWidth
        let height = envInt("WIRED_MONITOR_VIRTUAL_HEIGHT", min: 360, max: 16_384) ?? clientHeight
        let refreshRate = envInt("WIRED_MONITOR_VIRTUAL_REFRESH", min: 24, max: 120) ?? clientRefresh
        let backingScale = virtualBackingScale(width: width, height: height, dpi: clientInfo?.dpi ?? 0)
        let hiDPI = backingScale > 1
        let logicalWidth = envInt("WIRED_MONITOR_LOGICAL_WIDTH", min: 320, max: 16_384) ?? max(320, width / backingScale)
        let logicalHeight = envInt("WIRED_MONITOR_LOGICAL_HEIGHT", min: 180, max: 16_384) ?? max(180, height / backingScale)
        let ppi = envInt("WIRED_MONITOR_VIRTUAL_PPI", min: 50, max: 500) ?? (hiDPI ? 220 : 110)
        let name = ProcessInfo.processInfo.environment["WIRED_MONITOR_DISPLAY_NAME"] ?? "Wired Monitor"

        return VirtualDisplayConfiguration(
            enabled: enabled,
            width: alignVideoDimension(width),
            height: alignVideoDimension(height),
            logicalWidth: logicalWidth,
            logicalHeight: logicalHeight,
            refreshRate: refreshRate,
            pixelsPerInch: ppi,
            hiDPI: hiDPI,
            backingScale: backingScale,
            name: name)
    }

    private static func virtualBackingScale(width: Int, height: Int, dpi: Int) -> Int {
        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_RETINA"],
           let parsed = Boolish(value) {
            return parsed ? 2 : 1
        }

        if let value = ProcessInfo.processInfo.environment["WIRED_MONITOR_HIDPI"],
           let parsed = Boolish(value) {
            return parsed ? 2 : 1
        }

        if let scale = envInt("WIRED_MONITOR_BACKING_SCALE", min: 1, max: 3) {
            return scale
        }

        if dpi >= 144 {
            return 2
        }

        if dpi > 0 {
            return 1
        }

        return width >= 2560 || height >= 1440 ? 2 : 1
    }

    private static func envInt(_ key: String, min: Int, max: Int) -> Int? {
        guard let value = ProcessInfo.processInfo.environment[key],
              let parsed = Int(value),
              parsed >= min,
              parsed <= max else {
            return nil
        }
        return parsed
    }

    private static func validDimension(_ value: Int?) -> Bool {
        guard let value else { return false }
        return value >= 640 && value <= 16_384
    }

    private static func validRefreshRate(_ value: Int?) -> Bool {
        guard let value else { return false }
        return value >= 24 && value <= 120
    }

    private static func Boolish(_ value: String) -> Bool? {
        switch value.lowercased() {
        case "1", "true", "yes", "on":
            return true
        case "0", "false", "no", "off":
            return false
        default:
            return nil
        }
    }
}

final class VirtualDisplay {
    private let handle: WMVirtualDisplayHandle

    var displayID: CGDirectDisplayID {
        handle.displayID
    }

    private init(handle: WMVirtualDisplayHandle) {
        self.handle = handle
    }

    static func create(configuration: VirtualDisplayConfiguration) -> VirtualDisplay? {
        guard WMVirtualDisplayIsAvailable() else {
            print("[虚拟显示] 当前 macOS 不提供 CGVirtualDisplay 运行时类")
            return nil
        }

        var options = WMVirtualDisplayOptions()
        options.width = UInt32(configuration.width)
        options.height = UInt32(configuration.height)
        options.logicalWidth = UInt32(configuration.logicalWidth)
        options.logicalHeight = UInt32(configuration.logicalHeight)
        options.refreshRate = Double(configuration.refreshRate)
        options.pixelsPerInch = UInt32(configuration.pixelsPerInch)
        options.hiDPI = configuration.hiDPI

        return configuration.name.withCString { namePtr in
            options.name = namePtr
            var error: NSError?
            guard let handle = WMVirtualDisplayMake(options, &error) else {
                if let error {
                    print("[虚拟显示] 创建失败: \(error.localizedDescription)")
                } else {
                    print("[虚拟显示] 创建失败: 未知错误")
                }
                return nil
            }

            let display = VirtualDisplay(handle: handle)
            guard waitUntilOnline(displayID: display.displayID) else {
                print("[虚拟显示] displayID=\(display.displayID) 未出现在在线显示器列表中")
                DisplayDiagnostics.printOnlineDisplays(prefix: "[虚拟显示]")
                return nil
            }
            print("[虚拟显示] 已创建 \(configuration.width)x\(configuration.height) backing pixels, logical \(configuration.logicalWidth)x\(configuration.logicalHeight) @ \(configuration.refreshRate)Hz, displayID=\(display.displayID), HiDPI=\(configuration.hiDPI), scale=\(configuration.backingScale)x")
            DisplayDiagnostics.printOnlineDisplays(prefix: "[虚拟显示]")
            return display
        }
    }

    private static func waitUntilOnline(displayID: CGDirectDisplayID) -> Bool {
        let deadline = Date().addingTimeInterval(2)
        while Date() < deadline {
            if isOnline(displayID: displayID) {
                return true
            }
            Thread.sleep(forTimeInterval: 0.05)
        }
        return false
    }

    private static func isOnline(displayID: CGDirectDisplayID) -> Bool {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else {
            return false
        }

        var displays = Array(repeating: CGDirectDisplayID(), count: Int(count))
        guard CGGetOnlineDisplayList(count, &displays, &count) == .success else {
            return false
        }

        return displays.prefix(Int(count)).contains(displayID)
    }
}
