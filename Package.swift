// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "openrewind",
    platforms: [.macOS(.v14)],
    dependencies: [
        // SQLCipher (SQLite with encryption, FTS5 on) built from source and linked statically, so the
        // signed app needs no extra framework. Replaces the system libsqlite3. See "Encryption" in the README.
        .package(url: "https://github.com/skiptools/swift-sqlcipher.git", exact: "1.9.0"),
        // ONNX Runtime (MIT, Microsoft) runs NVIDIA Parakeet v3 (Settings → Transcription Model).
        // A static library linked into the app; the same version as the Windows app's NuGet package.
        .package(url: "https://github.com/microsoft/onnxruntime-swift-package-manager", exact: "1.24.2"),
    ],
    targets: [
        .executableTarget(
            name: "openrewind",
            dependencies: [
                .product(name: "SQLCipher", package: "swift-sqlcipher"),
                .product(name: "onnxruntime", package: "onnxruntime-swift-package-manager"),
            ],
            path: "Sources/openrewind",
            linkerSettings: [
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
