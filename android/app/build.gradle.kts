plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// The release workflow passes the semantic-release version in; the defaults keep local and CI
// builds working without it. There is no version to inherit from the .NET side — the whole repo is
// versioned by one `vX.Y.Z` tag, and the workflow derives the integer code from it.
val mhVersionName = (findProperty("mhVersionName") as String?)?.takeIf { it.isNotBlank() } ?: "1.0"
val mhVersionCode = (findProperty("mhVersionCode") as String?)?.toIntOrNull() ?: 1

// The host whose https share/invite links open this app (the autoVerify intent filter in the
// manifest). Intent-filter hosts are static per APK, so a self-hosted instance needs its own
// build: ./gradlew :app:assembleRelease -PmhShareHost=music.example.com — and its frontend must
// serve /.well-known/assetlinks.json with that build's signing fingerprint.
val mhShareHost = (findProperty("mhShareHost") as String?)?.takeIf { it.isNotBlank() } ?: "musichoarder.app"

// Release signing is configured only when a keystore is supplied — by the release workflow from
// repo secrets, or by a developer exporting the same variables. Without one, `assembleRelease`
// produces an unsigned APK that nobody can install, so CI publishes the debug build instead
// rather than an artifact that looks releasable and is not. See android/README.md.
val keystorePath: String? = System.getenv("MH_KEYSTORE_PATH")?.takeIf { it.isNotBlank() }

android {
    namespace = "com.musichoarder.app"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "com.musichoarder.app"
        minSdk = 24
        targetSdk = 37
        versionCode = mhVersionCode
        versionName = mhVersionName
        manifestPlaceholders["shareHost"] = mhShareHost

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (keystorePath != null) {
            create("release") {
                storeFile = file(keystorePath)
                storePassword = System.getenv("MH_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("MH_KEY_ALIAS")
                keyPassword = System.getenv("MH_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            optimization {
                enable = false
            }
            signingConfigs.findByName("release")?.let { signingConfig = it }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    buildFeatures {
        compose = true
    }
    testOptions {
        // Media3's extractors log through android.util.Log, which is an unimplemented stub in a
        // plain JVM test. Nothing here asserts on logging, so let the stubs return rather than throw.
        unitTests.isReturnDefaultValues = true
        unitTests.isIncludeAndroidResources = true
    }
}

dependencies {
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.extended)
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.credentials)
    implementation(libs.androidx.credentials.play.services.auth)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.media3.datasource.okhttp)
    implementation(libs.androidx.media3.exoplayer)
    implementation(libs.androidx.media3.session)
    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.okhttp)
    implementation(libs.play.services.code.scanner)
    testImplementation(libs.junit)
    testImplementation(libs.androidx.media3.test.utils)
    testImplementation(libs.robolectric)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.compose.ui.test.junit4)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
    debugImplementation(libs.androidx.compose.ui.test.manifest)
    debugImplementation(libs.androidx.compose.ui.tooling)
}
