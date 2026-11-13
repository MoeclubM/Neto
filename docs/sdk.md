# 上层客户端集成指南（SDK）

`XBClient` 等商业封装客户端无需复制任何代理内核、TUN 网卡、系统代理或连接/主页/节点 UI 代码，直接按平台引用 `Neto` 的原生 SDK 并叠加账号鉴权、套餐购买、工单与返佣页面即可。

## Android 集成

1. 在 `settings.gradle.kts` 中引入 `Neto` 构建：

```kotlin
includeBuild("deps/neto") {
    dependencySubstitution {
        substitute(module("moe.telecom.neto:sdk")).using(project(":sdk:android"))
    }
}
```

2. 在应用模块依赖 `implementation("moe.telecom.neto:sdk-android:0.0.5")`，即可直接复用：
   - `NetoControllerClient`（包含 `upsertManagedProfile`、`syncManagedSubscription`、`addCustomRule` 等接口）
   - `NetoVpnService`（通过 Manifest Merge 自动注册）
   - `NetoHomePane`（合并连接控制、节点切换、路由模式与可自定义状态卡片，支持 `extraHeaderSlot` 与 `blockedContent` 槽位）
   - `NetoNodesPane`、`NetoConnectionsPane`、`NetoRulesPane` 与 `NetoSettingsPane`

## Windows WinUI 3 集成

1. 在 `.csproj` 中添加项目引用：

```xml
<ProjectReference Include="..\..\deps\neto\sdk\windows\Neto.Sdk.Windows.csproj" />
```

2. 将构建生成的 `neto_ffi.dll`（或聚合导出库）、`wintun.dll` 与 `neto-service.exe`（Windows TUN 服务进程）放入输出目录，即可直接使用 `NetoClient`、`SingleInstanceGuard`、`NetoWindowHelper`、`Win32TrayIcon`、`HomePageView`、`NodesPageView`、`ConnectionsPageView`、`RulesPageView` 与 `SettingsPageView`。

## Linux Wayland (GTK4 + libadwaita) 集成

在 `Cargo.toml` 中引用 `neto-core`、`neto-platform-linux` 与 `neto-ui-gtk`，将 `HomeView`、`NodesView`、`ConnectionsView`、`RulesView`、`SettingsView` 与自定义商业面板视图共同挂载到 `adw::ViewStack` 即可。
