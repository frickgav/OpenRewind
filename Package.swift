// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "openrewind",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "openrewind",
            path: "Sources/openrewind",
            linkerSettings: [
                .linkedLibrary("sqlite3"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Vision"),
                .linkedFramework("AppKit"),
                .linkedFramework("Network"),
                .linkedFramework("NaturalLanguage"),
                .linkedFramework("IOKit"),
            ]
        ),
    ]
)
