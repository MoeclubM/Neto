# Neto

`Neto` 是基于 `Aerion` 内核的多端原生订阅与网络连接客户端，同时作为可复用的客户端基础库供上层商业客户端（如 `XBClient`）集成。

## 核心特性

- **逻辑共用、界面与系统能力原生分离**：
  - 共享核心层（`crates/neto-core`、`crates/neto-ffi`）：统一负责远程订阅拉取（`Aerion` / `Clash.Meta` YAML、`subscription-userinfo` 配额解析、远程 `rule-providers` 规则展开）、本地 YAML 导入、DoH 与 `Flux` 多通道静态解析、SOCKS5 / 规则路由 / TUN 会话管理、本地 HTTP/CONNECT 混合代理、60 秒速率与延迟采样、连接日志。
  - 原生系统层（`neto-platform-android`、`neto-platform-windows`、`neto-platform-linux`）：分别实现 Android `VpnService` 套接字保护与底层物理网络绑定、Windows Win32 `WinINet` 系统代理与注册表自启、Linux Wayland 会话优先、`gsettings` / `kwriteconfig` 系统代理与 `StatusNotifierItem` 原生托盘。
  - 原生界面层：Android 采用 Jetpack Compose + Miuix，Windows 采用 WinUI 3（.NET 9），Linux Wayland 采用 GTK4 + libadwaita。
- **清晰的导航与页面职责**：
  - 底栏独立导航：`连接`、`主页`、`节点`、`订阅`、`设置`。
  - **连接页**：无页面标题干扰，聚焦连接状态指示、实时连接时长、主连接开关、当前节点快捷跳转、规则/全局/直连模式切换及实时速率与延迟曲线。
  - **可自定义主页**：支持实时速率、出站模式、当前节点、网络接管、流量统计、订阅概览、连通延迟、运行信息等模块化卡片的显示开关、上下排序与一键恢复默认。
  - **独立节点页**：位于底栏一级入口，支持策略组选择、关键字筛选、默认/延迟/名称排序、批量并发测速与单节点延迟探测。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `crates/neto-core` | 跨平台核心状态机、订阅解析、路由/TUN 会话控制、本地混合代理、实时遥测与主页卡片模型 |
| `crates/neto-platform-android` | Android JNI `VpnService` 套接字保护与日志事件桥接 |
| `crates/neto-platform-windows` | Windows Win32 `WinINet` 系统代理备份恢复、管理员令牌检测与注册表自启 |
| `crates/neto-platform-linux` | Linux Wayland 后端优先、GNOME/KDE 系统代理、XDG 自启与 `StatusNotifierItem` D-Bus 托盘 |
| `crates/neto-ffi` | 面向 Windows P/Invoke 与 Android JNI 的统一 C ABI / JNI 导出层 |
| `crates/neto-ui-gtk` | Linux Wayland GTK4 + libadwaita 可复用原生视图组件库 |
| `sdk/android` | Android Library（`moe.telecom.neto:sdk`），提供 `NetoVpnService`、`NetoControllerClient` 与 Compose 页面组件 |
| `sdk/windows` | Windows WinUI 3 类库（`Neto.Sdk.Windows`），提供 P/Invoke 客户端、Win32 托盘与可复用原生页面组件 |
| `apps/android` | 独立 Android 客户端（`moe.telecom.neto`） |
| `apps/windows` | 独立 Windows WinUI 3 客户端（`Neto.exe`，提供原生安装程序 `-setup.exe` 与免安装便携包 `-windows-x64.zip`） |
| `apps/linux` | 独立 Linux Wayland GTK4/libadwaita 客户端（`neto`） |
| `third_party/` | 经过补丁适配的 `tun2proxy` 与 `socks5-impl` |

## 版本规范

在正式版（`1.0.0`）发布前，版本号统一采用 `0.0.x` 递增（自 `v0.0.1` 起）。

## 文档索引

- [架构说明](docs/architecture.md)
- [上层客户端集成指南（SDK）](docs/sdk.md)

## 开源许可

Apache License 2.0，详见 `LICENSE` 与 `NOTICE`。
