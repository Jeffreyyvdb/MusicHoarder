// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MusichoarderNativeAudio",
    platforms: [.iOS(.v15)],
    products: [
        .library(name: "MusichoarderNativeAudio", targets: ["NativeAudioPlugin"]),
        // What the Home Screen widgets and the app share. The widget extension links this alone:
        // the plugin target needs Capacitor, which an extension must not load.
        .library(name: "MusichoarderWidgetBridge", targets: ["WidgetBridge"])
    ],
    dependencies: [
        .package(url: "https://github.com/ionic-team/capacitor-swift-pm.git", from: "8.0.0")
    ],
    targets: [
        .target(
            name: "WidgetBridge",
            path: "ios/Sources/WidgetBridge"
        ),
        .target(
            name: "NativeAudioPlugin",
            dependencies: [
                "WidgetBridge",
                .product(name: "Capacitor", package: "capacitor-swift-pm"),
                .product(name: "Cordova", package: "capacitor-swift-pm")
            ],
            path: "ios/Sources/NativeAudioPlugin",
            resources: [.copy("PrivacyInfo.xcprivacy")]
        ),
        .testTarget(
            name: "WidgetBridgeTests",
            dependencies: ["WidgetBridge"],
            path: "ios/Tests/WidgetBridgeTests"
        )
    ]
)
