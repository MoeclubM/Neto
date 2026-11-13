plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

val appMinApi = 33
val compileApi = 37
val targetApi = 36

fun gitText(vararg args: String): String = runCatching {
    val process = ProcessBuilder("git", *args)
        .directory(rootProject.projectDir)
        .redirectErrorStream(true)
        .start()
    val output = process.inputStream.bufferedReader().use { it.readText() }.trim()
    if (process.waitFor() == 0) output else ""
}.getOrDefault("")

val gitCommitTimestamp = gitText("log", "-1", "--format=%ct").toIntOrNull() ?: 1
val gitShortHash = gitText("rev-parse", "--short=8", "HEAD").ifEmpty { "dev" }
val gitExactTag = gitText("describe", "--tags", "--exact-match", "HEAD")
val gitLatestTag = gitText("describe", "--tags", "--abbrev=0")
val gitCommitsSinceLatestTag = if (gitLatestTag.isNotEmpty()) {
    gitText("rev-list", "$gitLatestTag..HEAD", "--count").ifEmpty { "0" }
} else {
    ""
}
val appVersionCode = gitCommitTimestamp
val appVersionName = gitExactTag.removePrefix("v").ifEmpty {
    if (gitLatestTag.isNotEmpty()) {
        "${gitLatestTag.removePrefix("v")}-beta.$gitCommitsSinceLatestTag.$gitShortHash"
    } else {
        "0.0.3-$gitShortHash"
    }
}

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
        versionCode = appVersionCode
        versionName = appVersionName
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
