// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "openscreen",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "openscreen",
            path: "Sources/openscreen",
            linkerSettings: [
                .linkedLibrary("sqlite3"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Vision"),
                .linkedFramework("AppKit"),
                .linkedFramework("Network"),
            ]
        ),
    ]
)
