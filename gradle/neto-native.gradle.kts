import java.util.Locale

fun netoSdkDirectory(): File {
    System.getenv("ANDROID_SDK_ROOT")?.let { return file(it) }
    System.getenv("ANDROID_HOME")?.let { return file(it) }
    error("Android SDK directory is not configured")
}

val netoMinAndroidApi = extensions.extraProperties["netoMinAndroidApi"] as Int
val netoAndroidNdkVersion = extensions.extraProperties["netoAndroidNdkVersion"] as String
val netoWorkspaceDir = project.projectDir.resolve("../..").canonicalFile
val netoFfiDir = netoWorkspaceDir.resolve("crates/neto-ffi")
val netoCoreDir = netoWorkspaceDir.resolve("crates/neto-core")
val netoAndroidPlatformDir = netoWorkspaceDir.resolve("crates/neto-platform-android")
val netoLibraryName = "neto_ffi"
val netoAndroidSdkDir = netoSdkDirectory()
val netoAndroidNdkDir = netoAndroidSdkDir.resolve("ndk/$netoAndroidNdkVersion")
val netoHostOs = System.getProperty("os.name").lowercase(Locale.ROOT)
val netoIsWindows = netoHostOs.contains("windows")
val netoNdkHostTag = when {
    netoIsWindows -> "windows-x86_64"
    netoHostOs.contains("mac") -> "darwin-x86_64"
    else -> "linux-x86_64"
}
val netoExeSuffix = if (netoIsWindows) ".cmd" else ""
val netoToolSuffix = if (netoIsWindows) ".exe" else ""
val netoNdkBinDir = netoAndroidNdkDir.resolve("toolchains/llvm/prebuilt/$netoNdkHostTag/bin")
val netoRustFlags = "-C link-arg=-Wl,-z,max-page-size=16384"
val netoTargets = listOf(
    Triple("arm64-v8a", "aarch64-linux-android", "aarch64-linux-android${netoMinAndroidApi}-clang$netoExeSuffix"),
)

fun netoRustEnvName(target: String): String =
    "CARGO_TARGET_${target.uppercase(Locale.ROOT).replace('-', '_')}_LINKER"

fun netoCcEnvName(prefix: String, target: String): String =
    "${prefix}_${target.replace('-', '_')}"

netoTargets.forEach { (abi, target, clang) ->
    val taskSuffix = abi.replace("-", "_")
    val cargoTask = tasks.register<Exec>("cargoBuildNeto$taskSuffix") {
        workingDir = netoWorkspaceDir
        inputs.files(
            netoWorkspaceDir.resolve("Cargo.toml"),
            fileTree(netoCoreDir.resolve("src")),
            netoCoreDir.resolve("Cargo.toml"),
            fileTree(netoAndroidPlatformDir.resolve("src")),
            netoAndroidPlatformDir.resolve("Cargo.toml"),
            fileTree(netoFfiDir.resolve("src")),
            netoFfiDir.resolve("Cargo.toml"),
        )
        inputs.property("androidNdkVersion", netoAndroidNdkVersion)
        inputs.property("minAndroidApi", netoMinAndroidApi)
        inputs.property("rustTarget", target)
        outputs.file(netoWorkspaceDir.resolve("target/$target/release/lib$netoLibraryName.so"))
        val cc = netoNdkBinDir.resolve(clang).absolutePath
        val cxx = cc.removeSuffix("clang$netoExeSuffix") + "clang++$netoExeSuffix"
        environment(netoRustEnvName(target), cc)
        environment(netoCcEnvName("CC", target), cc)
        environment(netoCcEnvName("CXX", target), cxx)
        environment(netoCcEnvName("AR", target), netoNdkBinDir.resolve("llvm-ar$netoToolSuffix").absolutePath)
        environment("ANDROID_NDK_HOME", netoAndroidNdkDir.absolutePath)
        environment("RUSTFLAGS", netoRustFlags)
        commandLine("cargo", "build", "--release", "-p", "neto-ffi", "--target", target)
    }

    tasks.register<Copy>("copyNeto$taskSuffix") {
        dependsOn(cargoTask)
        from(netoWorkspaceDir.resolve("target/$target/release/lib$netoLibraryName.so"))
        into(layout.buildDirectory.dir("generated/netoJniLibs/$abi"))
    }
}

val copyNetoJniLibs = tasks.register("copyNetoJniLibs") {
    dependsOn(netoTargets.map { (abi, _, _) -> "copyNeto${abi.replace("-", "_")}" })
}

tasks.matching { it.name.startsWith("merge") && it.name.endsWith("JniLibFolders") }.configureEach {
    dependsOn(copyNetoJniLibs)
}
