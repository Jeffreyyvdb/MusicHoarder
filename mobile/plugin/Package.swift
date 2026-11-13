// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MusichoarderNativeAudio",
    platforms: [.iOS(.v15)],
    products: [
        .library(name: "MusichoarderNativeAudio", targets: ["NativeAudioPlugin"])
    ],
    dependencies: [
        .package(url: "https://github.com/ionic-team/capacitor-swift-pm.git", from: "8.0.0")
    ],
    targets: [
        .target(
            name: "NativeAudioPlugin",
            dependencies: [
                .product(name: "Capacitor", package: "capacitor-swift-pm"),
                .product(name: "Cordova", package: "capacitor-swift-pm")
            ],
            path: "ios/Sources/NativeAudioPlugin"
        )
    ]
)
