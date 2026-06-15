import ApplicationServices
import CoreGraphics
import Foundation

private enum WindowsInputEventType: UInt8 {
    case mouseMove = 0x01
    case mouseDown = 0x02
    case mouseUp = 0x03
    case keyDown = 0x04
    case keyUp = 0x05
    case scroll = 0x06
}

final class WindowsControlForwarder {
    private static let hotkeyCodeW: UInt16 = 13
    private static let eventCallback: CGEventTapCallBack = { proxy, type, event, userInfo in
        guard let userInfo else {
            return Unmanaged.passUnretained(event)
        }

        let forwarder = Unmanaged<WindowsControlForwarder>.fromOpaque(userInfo).takeUnretainedValue()
        return forwarder.handleEvent(proxy: proxy, type: type, event: event)
    }

    private let server: FrameServer
    private let displayID: CGDirectDisplayID
    private let targetWidth: Int
    private let targetHeight: Int
    private var eventTap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var isControlModeEnabled = false
    private var suppressHotkeyKeyUp = false
    private var activeModifierKeys: Set<UInt16> = []
    private var targetModifierCounts: [UInt16: Int] = [:]

    init(server: FrameServer, displayID: CGDirectDisplayID, targetWidth: Int, targetHeight: Int) {
        self.server = server
        self.displayID = displayID
        self.targetWidth = max(1, targetWidth)
        self.targetHeight = max(1, targetHeight)
    }

    func start() {
        guard eventTap == nil else { return }
        guard hasInputPermissions() else { return }

        let mask = eventMask([
            .keyDown,
            .keyUp,
            .flagsChanged,
            .leftMouseDown,
            .leftMouseUp,
            .rightMouseDown,
            .rightMouseUp,
            .otherMouseDown,
            .otherMouseUp,
            .mouseMoved,
            .leftMouseDragged,
            .rightMouseDragged,
            .otherMouseDragged,
            .scrollWheel,
        ])

        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: Self.eventCallback,
            userInfo: Unmanaged.passUnretained(self).toOpaque())
        else {
            print("\(ServerText.controlTag) \(ServerText.text("无法创建输入监听；请检查辅助功能/输入监控权限", "Failed to create input event tap; check Accessibility/Input Monitoring permissions"))")
            return
        }

        guard let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0) else {
            CFMachPortInvalidate(tap)
            print("\(ServerText.controlTag) \(ServerText.text("无法创建输入监听 RunLoop source", "Failed to create input event tap RunLoop source"))")
            return
        }

        eventTap = tap
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        print("\(ServerText.controlTag) \(ServerText.text("Windows 控制模式热键已启用: Ctrl+Option+Command+W", "Windows control hotkey enabled: Ctrl+Option+Command+W"))")
    }

    func stop() {
        setControlMode(false, notifyClient: true)
        if let source = runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes)
        }
        if let eventTap {
            CFMachPortInvalidate(eventTap)
        }
        runLoopSource = nil
        eventTap = nil
    }

    func exitControlModeAfterDisconnect() {
        DispatchQueue.main.async { [weak self] in
            self?.setControlMode(false, notifyClient: false)
        }
    }

    private func hasInputPermissions() -> Bool {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        let accessibilityTrusted = AXIsProcessTrustedWithOptions(options)
        var listenTrusted = true

        if #available(macOS 10.15, *) {
            listenTrusted = CGPreflightListenEventAccess()
            if !listenTrusted {
                _ = CGRequestListenEventAccess()
            }
        }

        if !accessibilityTrusted || !listenTrusted {
            print("\(ServerText.controlTag) \(ServerText.text("Windows 控制模式不可用：请给启动服务端的终端/App 授予辅助功能和输入监控权限", "Windows control mode unavailable: grant Accessibility and Input Monitoring permissions to the terminal/app running the server"))")
            return false
        }

        return true
    }

    private func handleEvent(proxy _: CGEventTapProxy, type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let eventTap {
                CGEvent.tapEnable(tap: eventTap, enable: true)
            }
            return Unmanaged.passUnretained(event)
        }

        if type == .keyDown, isToggleHotkey(event) {
            suppressHotkeyKeyUp = true
            setControlMode(!isControlModeEnabled, notifyClient: true)
            return nil
        }

        if type == .keyUp,
           suppressHotkeyKeyUp,
           keyCode(from: event) == Self.hotkeyCodeW {
            suppressHotkeyKeyUp = false
            return nil
        }

        guard isControlModeEnabled else {
            return Unmanaged.passUnretained(event)
        }

        switch type {
        case .keyDown:
            forwardKey(event, isDown: true)
        case .keyUp:
            forwardKey(event, isDown: false)
        case .flagsChanged:
            if shouldPassThroughUnforwardedModifierRelease(event) {
                return Unmanaged.passUnretained(event)
            }
            forwardModifier(event)
        case .leftMouseDown, .rightMouseDown, .otherMouseDown:
            forwardMouseButton(type: type, event: event, isDown: true)
        case .leftMouseUp, .rightMouseUp, .otherMouseUp:
            forwardMouseButton(type: type, event: event, isDown: false)
        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            forwardMouseMove(event)
        case .scrollWheel:
            forwardScroll(event)
        default:
            break
        }

        return nil
    }

    private func isToggleHotkey(_ event: CGEvent) -> Bool {
        guard keyCode(from: event) == Self.hotkeyCodeW else { return false }
        let flags = event.flags
        return flags.contains(.maskCommand) &&
            flags.contains(.maskAlternate) &&
            flags.contains(.maskControl)
    }

    private func setControlMode(_ enabled: Bool, notifyClient: Bool) {
        guard enabled != isControlModeEnabled else { return }

        if !enabled {
            releaseForwardedModifiers()
        }

        isControlModeEnabled = enabled
        if notifyClient {
            server.sendWindowsControlMode(enabled: enabled)
        }

        print(enabled
            ? "\(ServerText.controlTag) \(ServerText.text("已进入 Windows 控制模式", "Entered Windows control mode"))"
            : "\(ServerText.controlTag) \(ServerText.text("已退出 Windows 控制模式", "Exited Windows control mode"))")
    }

    private func forwardKey(_ event: CGEvent, isDown: Bool) {
        guard let virtualKey = windowsVirtualKey(forMacKeyCode: keyCode(from: event)) else { return }
        sendKey(virtualKey, isDown: isDown)
    }

    private func forwardModifier(_ event: CGEvent) {
        let macKey = keyCode(from: event)
        guard let targetKey = windowsModifierKey(forMacKeyCode: macKey) else { return }

        let isDown = isMacModifierDown(macKey, flags: event.flags)
        let wasDown = activeModifierKeys.contains(macKey)
        if isDown == wasDown {
            return
        }

        if isDown {
            activeModifierKeys.insert(macKey)
            let count = targetModifierCounts[targetKey, default: 0]
            targetModifierCounts[targetKey] = count + 1
            if count == 0 {
                sendKey(targetKey, isDown: true)
            }
        } else {
            activeModifierKeys.remove(macKey)
            let count = max(0, targetModifierCounts[targetKey, default: 0] - 1)
            targetModifierCounts[targetKey] = count
            if count == 0 {
                sendKey(targetKey, isDown: false)
            }
        }
    }

    private func shouldPassThroughUnforwardedModifierRelease(_ event: CGEvent) -> Bool {
        let macKey = keyCode(from: event)
        guard windowsModifierKey(forMacKeyCode: macKey) != nil else { return false }

        return !isMacModifierDown(macKey, flags: event.flags) &&
            !activeModifierKeys.contains(macKey)
    }

    private func releaseForwardedModifiers() {
        for key in targetModifierCounts.keys where targetModifierCounts[key, default: 0] > 0 {
            sendKey(key, isDown: false)
        }
        activeModifierKeys.removeAll()
        targetModifierCounts.removeAll()
    }

    private func forwardMouseMove(_ event: CGEvent) {
        let position = mappedPosition(from: event)
        var payload = Data(capacity: 9)
        payload.append(WindowsInputEventType.mouseMove.rawValue)
        appendInt32(Int32(position.x), to: &payload)
        appendInt32(Int32(position.y), to: &payload)
        server.sendWindowsInput(data: payload)
    }

    private func forwardMouseButton(type: CGEventType, event: CGEvent, isDown: Bool) {
        guard let button = mouseButton(for: type, event: event) else { return }
        let position = mappedPosition(from: event)
        var payload = Data(capacity: 10)
        payload.append(isDown ? WindowsInputEventType.mouseDown.rawValue : WindowsInputEventType.mouseUp.rawValue)
        payload.append(button)
        appendInt32(Int32(position.x), to: &payload)
        appendInt32(Int32(position.y), to: &payload)
        server.sendWindowsInput(data: payload)
    }

    private func forwardScroll(_ event: CGEvent) {
        let deltaY = Int32(event.getIntegerValueField(.scrollWheelEventDeltaAxis1))
        let deltaX = Int32(event.getIntegerValueField(.scrollWheelEventDeltaAxis2))
        guard deltaX != 0 || deltaY != 0 else { return }

        var payload = Data(capacity: 9)
        payload.append(WindowsInputEventType.scroll.rawValue)
        appendInt32(deltaX, to: &payload)
        appendInt32(deltaY, to: &payload)
        server.sendWindowsInput(data: payload)
    }

    private func sendKey(_ virtualKey: UInt16, isDown: Bool) {
        var payload = Data(capacity: 3)
        payload.append(isDown ? WindowsInputEventType.keyDown.rawValue : WindowsInputEventType.keyUp.rawValue)
        appendUInt16(virtualKey, to: &payload)
        server.sendWindowsInput(data: payload)
    }

    private func mappedPosition(from event: CGEvent) -> (x: Int, y: Int) {
        let bounds = CGDisplayBounds(displayID)
        guard bounds.width > 0, bounds.height > 0 else {
            return (0, 0)
        }

        let location = event.location
        let normalizedX = (location.x - bounds.origin.x) / bounds.width
        let normalizedY = (location.y - bounds.origin.y) / bounds.height
        let x = clamp(Int((normalizedX * CGFloat(targetWidth)).rounded(.down)), min: 0, max: targetWidth - 1)
        let y = clamp(Int((normalizedY * CGFloat(targetHeight)).rounded(.down)), min: 0, max: targetHeight - 1)
        return (x, y)
    }

    private func keyCode(from event: CGEvent) -> UInt16 {
        UInt16(event.getIntegerValueField(.keyboardEventKeycode))
    }

    private func mouseButton(for type: CGEventType, event: CGEvent) -> UInt8? {
        switch type {
        case .leftMouseDown, .leftMouseUp:
            return 1
        case .rightMouseDown, .rightMouseUp:
            return 2
        case .otherMouseDown, .otherMouseUp:
            let buttonNumber = event.getIntegerValueField(.mouseEventButtonNumber)
            if buttonNumber == 2 { return 3 }
            if buttonNumber == 3 { return 4 }
            if buttonNumber == 4 { return 5 }
            return nil
        default:
            return nil
        }
    }

    private func isMacModifierDown(_ keyCode: UInt16, flags: CGEventFlags) -> Bool {
        switch keyCode {
        case 55, 54:
            return flags.contains(.maskCommand)
        case 56, 60:
            return flags.contains(.maskShift)
        case 58, 61:
            return flags.contains(.maskAlternate)
        case 59, 62:
            return flags.contains(.maskControl)
        default:
            return false
        }
    }

    private func windowsModifierKey(forMacKeyCode keyCode: UInt16) -> UInt16? {
        switch keyCode {
        case 55, 54:
            return 0xA2 // Command -> Left Ctrl
        case 56:
            return 0xA0 // Left Shift
        case 60:
            return 0xA1 // Right Shift
        case 58:
            return 0xA4 // Left Alt
        case 61:
            return 0xA5 // Right Alt
        case 59, 62:
            return 0xA2 // Control -> Left Ctrl
        default:
            return nil
        }
    }

    private func windowsVirtualKey(forMacKeyCode keyCode: UInt16) -> UInt16? {
        switch keyCode {
        case 0: return 0x41
        case 1: return 0x53
        case 2: return 0x44
        case 3: return 0x46
        case 4: return 0x48
        case 5: return 0x47
        case 6: return 0x5A
        case 7: return 0x58
        case 8: return 0x43
        case 9: return 0x56
        case 11: return 0x42
        case 12: return 0x51
        case 13: return 0x57
        case 14: return 0x45
        case 15: return 0x52
        case 16: return 0x59
        case 17: return 0x54
        case 18: return 0x31
        case 19: return 0x32
        case 20: return 0x33
        case 21: return 0x34
        case 22: return 0x36
        case 23: return 0x35
        case 24: return 0xBB
        case 25: return 0x39
        case 26: return 0x37
        case 27: return 0xBD
        case 28: return 0x38
        case 29: return 0x30
        case 30: return 0xDD
        case 31: return 0x4F
        case 32: return 0x55
        case 33: return 0xDB
        case 34: return 0x49
        case 35: return 0x50
        case 36: return 0x0D
        case 37: return 0x4C
        case 38: return 0x4A
        case 39: return 0xDE
        case 40: return 0x4B
        case 41: return 0xBA
        case 42: return 0xDC
        case 43: return 0xBC
        case 44: return 0xBF
        case 45: return 0x4E
        case 46: return 0x4D
        case 47: return 0xBE
        case 48: return 0x09
        case 49: return 0x20
        case 50: return 0xC0
        case 51: return 0x08
        case 53: return 0x1B
        case 65: return 0x6E
        case 67: return 0x6A
        case 69: return 0x6B
        case 71: return 0x90
        case 75: return 0x6F
        case 76: return 0x0D
        case 78: return 0x6D
        case 81: return 0x6D
        case 82: return 0x60
        case 83: return 0x61
        case 84: return 0x62
        case 85: return 0x63
        case 86: return 0x64
        case 87: return 0x65
        case 88: return 0x66
        case 89: return 0x67
        case 91: return 0x68
        case 92: return 0x69
        case 96: return 0x74
        case 97: return 0x75
        case 98: return 0x76
        case 99: return 0x72
        case 100: return 0x77
        case 101: return 0x78
        case 103: return 0x7A
        case 109: return 0x79
        case 111: return 0x7B
        case 114: return 0x2D
        case 115: return 0x24
        case 116: return 0x21
        case 117: return 0x2E
        case 118: return 0x73
        case 119: return 0x23
        case 120: return 0x71
        case 121: return 0x22
        case 122: return 0x70
        case 123: return 0x25
        case 124: return 0x27
        case 125: return 0x28
        case 126: return 0x26
        default: return nil
        }
    }

    private func appendUInt16(_ value: UInt16, to data: inout Data) {
        var littleEndian = value.littleEndian
        data.append(Data(bytes: &littleEndian, count: 2))
    }

    private func appendInt32(_ value: Int32, to data: inout Data) {
        var littleEndian = value.littleEndian
        data.append(Data(bytes: &littleEndian, count: 4))
    }

    private func clamp(_ value: Int, min lower: Int, max upper: Int) -> Int {
        Swift.max(lower, Swift.min(value, upper))
    }

    private func eventMask(_ types: [CGEventType]) -> CGEventMask {
        types.reduce(CGEventMask(0)) { mask, type in
            mask | (CGEventMask(1) << CGEventMask(type.rawValue))
        }
    }
}
