plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.plugin.compose")
}

group = "moe.telecom.neto"
version = "0.1.0"

val minNdkApi = 26
val appMinApi = 33
val compileApi = 37
val androidNdkVer = "28.2.13676358"
val miuixVersion = "0.9.4"

extensions.extraProperties["netoMinAndroidApi"] = minNdkApi
extensions.extraProperties["netoAndroidNdkVersion"] = androidNdkVer

android {
    namespace = "moe.telecom.neto.sdk"
    compileSdk {
        version = release(compileApi) {
            minorApiLevel = 0
        }
    }
    buildToolsVersion = "36.1.0"
    ndkVersion = androidNdkVer

    defaultConfig {
        minSdk = appMinApi
        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    buildFeatures {
        compose = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

val netoRepoDir = projectDir.resolve("../..").canonicalFile
android.sourceSets.getByName("main").jniLibs.directories.add(
    layout.buildDirectory.file("generated/netoJniLibs").get().asFile.absolutePath
)
android.sourceSets.getByName("main").assets.srcDir(
    netoRepoDir.resolve("crates/neto-core/assets")
)
apply(from = netoRepoDir.resolve("gradle/neto-native.gradle.kts"))

dependencies {
    api(platform("androidx.compose:compose-bom:2026.09.00"))
    api("androidx.activity:activity-compose:1.13.0")
    api("androidx.compose.material:material-icons-extended")
    api("androidx.compose.ui:ui")
    api("androidx.lifecycle:lifecycle-runtime-ktx:2.11.0")
    api("androidx.lifecycle:lifecycle-runtime-compose:2.11.0")
    api("androidx.lifecycle:lifecycle-viewmodel-ktx:2.11.0")
    api("top.yukonga.miuix.kmp:miuix-ui-android:$miuixVersion")
    api("top.yukonga.miuix.kmp:miuix-preference-android:$miuixVersion")
    api("top.yukonga.miuix.kmp:miuix-icons-android:$miuixVersion")
    api("top.yukonga.miuix.kmp:miuix-blur-android:$miuixVersion")
    api("top.yukonga.miuix.kmp:miuix-shader-android:$miuixVersion")
}
