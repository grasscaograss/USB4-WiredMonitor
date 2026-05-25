// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "WiredMonitorServer",
    platforms: [.macOS(.v13)],
    targets: [
        .target(
            name: "VirtualDisplayBridge",
            path: "Bridge/VirtualDisplayBridge",
            publicHeadersPath: "include",
            linkerSettings: [
                .linkedFramework("CoreGraphics"),
                .linkedFramework("Foundation"),
            ]
        ),
        .executableTarget(
            name: "WiredMonitorServer",
            dependencies: ["VirtualDisplayBridge"],
            path: "Sources"
        )
    ]
)
