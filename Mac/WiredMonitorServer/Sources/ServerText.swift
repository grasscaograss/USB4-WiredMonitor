import Foundation

enum ServerText {
    private static let forceLanguage = ProcessInfo.processInfo.environment["WIRED_MONITOR_LANG"]?
        .trimmingCharacters(in: .whitespacesAndNewlines)
        .lowercased()
        .replacingOccurrences(of: "_", with: "-")

    static let isChinese: Bool = {
        if let forceLanguage, !forceLanguage.isEmpty {
            return forceLanguage == "zh" ||
                forceLanguage == "zh-cn" ||
                forceLanguage == "zh-hans" ||
                forceLanguage == "cn" ||
                forceLanguage == "chinese"
        }

        let preferred = Locale.preferredLanguages.first?.lowercased() ?? ""
        return preferred.hasPrefix("zh")
    }()

    static func text(_ zh: String, _ en: String) -> String {
        isChinese ? zh : en
    }

    static func print(_ zh: String, _ en: String) {
        Swift.print(text(zh, en))
    }

    static var mainTag: String { text("[主]", "[Main]") }
    static var serverTag: String { text("[服务端]", "[Server]") }
    static var captureTag: String { text("[捕获]", "[Capture]") }
    static var encoderTag: String { text("[编码器]", "[Encoder]") }
    static var cursorTag: String { text("[鼠标]", "[Cursor]") }
    static var virtualDisplayTag: String { text("[虚拟显示]", "[VirtualDisplay]") }
    static var statsTag: String { text("[统计]", "[Stats]") }
    static var rawStatsTag: String { text("[统计-RAW]", "[Stats-RAW]") }
    static var captureStatsTag: String { text("[捕获统计]", "[CaptureStats]") }
    static var rawCaptureStatsTag: String { text("[捕获统计-RAW]", "[CaptureStats-RAW]") }

    static var bannerLines: [String] {
        if isChinese {
            return [
                "╔══════════════════════════════════════════╗",
                "║   Wired Monitor Server - Mac 扩展屏      ║",
                "║   通过 Thunderbolt/USB4 传输屏幕画面      ║",
                "╚══════════════════════════════════════════╝",
            ]
        }

        return [
            "╔══════════════════════════════════════════╗",
            "║   Wired Monitor Server - Mac Display     ║",
            "║   Screen stream over Thunderbolt/USB4    ║",
            "╚══════════════════════════════════════════╝",
        ]
    }
}
