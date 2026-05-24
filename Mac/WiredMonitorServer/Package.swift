// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "WiredMonitorServer",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "WiredMonitorServer",
            path: "Sources"
        )
    ]
)
