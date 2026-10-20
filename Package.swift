// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "openrewind",
    platforms: [.macOS(.v14)],
    dependencies: [
        // ONNX Runtime (MIT, Microsoft) runs NVIDIA Parakeet v3 (Settings → Transcription Model).
        // A static library linked into the app; the same version as the Windows app's NuGet package.
        .package(url: "https://github.com/microsoft/onnxruntime-swift-package-manager", exact: "1.24.2"),
    ],
    targets: [
        .executableTarget(
            name: "openrewind",
            dependencies: [
                .product(name: "onnxruntime", package: "onnxruntime-swift-package-manager"),
            ],
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
