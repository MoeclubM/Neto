plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

val appMinApi = 33
val compileApi = 37
val targetApi = 36

android {
    namespace = "moe.telecom.neto"
    compileSdk {
        version = release(compileApi) {
            minorApiLevel = 0
        }
    }
    buildToolsVersion = "36.1.0"
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "moe.telecom.neto"
        minSdk = appMinApi
        targetSdk = targetApi
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        getByName("release") {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        compose = true
    }

    splits {
        abi {
            isEnable = true
            isUniversalApk = false
            reset()
            include("arm64-v8a")
        }
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

dependencies {
    implementation(project(":sdk:android"))
}
