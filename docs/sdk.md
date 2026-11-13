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

2. 在应用模块依赖 `implementation("moe.telecom.neto:sdk:0.0.4")`，即可直接复用：
   - `NetoControllerClient`（包含 `upsertManagedProfile` 商业订阅注入接口）
   - `NetoVpnService`（通过 Manifest Merge 自动注册）
   - `NetoConnectPane`（支持传入 `blockedContent` 商业套餐到期提示槽位）
   - `NetoDashboardPane`（支持传入 `extraHeaderSlot` 公告/账户摘要槽位）
   - `NetoNodesPane`、`NetoSettingsPane` 与 `NetoDiagnosticsDialogs`（活跃会话、分流规则与匹配测试、分级运行日志弹窗）

## Windows WinUI 3 集成

1. 在 `.csproj` 中添加项目引用：

```xml
<ProjectReference Include="..\..\deps\neto\sdk\windows\Neto.Sdk.Windows.csproj" />
```

2. 将构建生成的 `neto_ffi.dll`（或聚合导出库）与 `wintun.dll` 放入输出目录，即可直接使用 `NetoClient`、`SingleInstanceGuard`、`NetoWindowHelper`、`Win32TrayIcon`、`ConnectPageView`、`DashboardPageView`、`NodesPageView`、`ConnectionsPageView` 与 `SettingsPageView`。

## Linux Wayland (GTK4 + libadwaita) 集成

在 `Cargo.toml` 中引用 `neto-core`、`neto-platform-linux` 与 `neto-ui-gtk`，将 `ConnectView`、`DashboardView`、`NodesView`、`ConnectionsView`、`SettingsView` 与自定义商业面板视图共同挂载到 `adw::ViewStack` 即可。
