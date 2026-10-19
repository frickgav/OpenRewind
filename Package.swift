// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "openrewind",
    platforms: [.macOS(.v14)],
    dependencies: [
        // SQLCipher (SQLite with encryption, FTS5 on) built from source and linked statically, so the
        // signed app needs no extra framework. Replaces the system libsqlite3. See "Encryption" in the README.
        .package(url: "https://github.com/skiptools/swift-sqlcipher.git", exact: "1.9.0"),
    ],
    targets: [
        .executableTarget(
            name: "openrewind",
            dependencies: [
                .product(name: "SQLCipher", package: "swift-sqlcipher"),
            ],
            path: "Sources/openrewind",
            linkerSettings: [
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Vision"),
                .linkedFramework("AppKit"),
                .linkedFramework("Network"),
            ]
        ),
    ]
)
