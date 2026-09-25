// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Brisa",
    platforms: [.macOS(.v13)],
    products: [.library(name: "BrisaCore", targets: ["BrisaCore"]), .executable(name: "Brisa", targets: ["Brisa"])],
    targets: [
        .target(name: "BrisaCore"),
        .executableTarget(name: "Brisa", dependencies: ["BrisaCore"]),
        .testTarget(name: "BrisaCoreTests", dependencies: ["BrisaCore"])
    ]
)
