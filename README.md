# Neto

`Neto` 是基于 `Aerion` 内核的多端原生订阅与网络连接客户端，同时作为可复用的客户端基础库供上层商业客户端（如 `XBClient`）集成。

## 核心特性

- **逻辑共用、界面与系统能力原生分离**：
  - 共享核心层（`crates/neto-core`、`crates/neto-ffi`）：统一负责远程订阅拉取与编辑（`Aerion` / `Clash.Meta` YAML、`subscription-userinfo` 配额解析、远程 `rule-providers` 规则展开）、本地 YAML 导入与编辑、DoH 与 `Flux` 多通道静态解析、多策略组自动选路（`select` / `url-test` / `fallback`）、SOCKS5 / 规则路由 / TUN 会话管理、HTTP/HTTPS/SOCKS5 同端口本地混合代理、TCP/UDP 实时活跃与已关闭会话追踪及单条/全部断开、分流规则检索与匹配测试、出口 IP 与多站点连通探测、分级日志过滤。
  - 原生系统层（`neto-platform-android`、`neto-platform-windows`、`crates/neto-service`、`neto-platform-linux`）：分别实现 Android `VpnService` 套接字保护与底层物理网络绑定、Windows `LocalSystem` 系统服务模式 TUN 接管（GUI 保持普通用户权限运行，通过命名管道控制 `neto-service.exe`）与 `WinINet` 系统代理及注册表自启、Linux Wayland 会话优先、`gsettings` / `kwriteconfig` 系统代理与 `StatusNotifierItem` 原生托盘。
  - 原生界面层：Android 采用 Jetpack Compose + Miuix，Windows 采用 WinUI 3（.NET 9），Linux Wayland 采用 GTK4 + libadwaita。
- **清晰的导航与页面职责**：
  - 桌面与移动端统一一级导航：`主页`、`节点`、`会话`、`规则`、`订阅`、`设置`。
  - **统一主页**：无页面标题干扰，顶部集中展示连接状态与时长、主连接开关、节点快速切换、规则/全局/直连模式（桌面端同时提供系统代理与 TUN 服务模式开关），下方支持实时速率、活跃会话、出口检测、流量统计、订阅概览、连通延迟、运行信息卡片的显示开关、上下排序与恢复默认。
  - **独立节点页**：位于底栏一级入口，支持手动与自动策略组状态查看及按组测速、协议与关键字筛选、当前节点定位、默认/延迟/名称排序、批量并发测速与单节点延迟探测。
  - **独立会话页**：支持活跃会话与已关闭历史记录切换，提供实时上下行速率、累计流量、持续时长、关键字与 TCP/UDP 筛选、多维度排序、暂停刷新、单条/全部断开及点击查看会话详情与复制目标主机。
  - **独立规则页**：分为分流规则检索（含目标域名/IP 路由匹配测试）与规则覆写（支持快捷置顶添加单条规则、完整 YAML 编辑保存、桌面端 YAML 文件导入及一键恢复默认）。
  - **分层订阅与设置**：主视图仅保留高频开关、混合端口与快捷订阅 URL 导入；DNS 参数、Windows TUN 系统服务状态管理、分应用代理、系统代理绕过与终端环境变量、本地 YAML 导入及运行日志统一收纳至二级页面。

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `crates/neto-core` | 跨平台核心状态机、订阅解析、路由/TUN 会话控制、本地混合代理、实时遥测与主页卡片模型 |
| `crates/neto-service` | Windows `LocalSystem` TUN 系统服务进程（`neto-service.exe`），通过命名管道承载 `wintun` 网卡会话 |
| `crates/neto-platform-android` | Android JNI `VpnService` 套接字保护与日志事件桥接 |
| `crates/neto-platform-windows` | Windows Win32 `WinINet` 系统代理备份恢复、TUN 服务安装/启动/卸载控制与注册表自启 |
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
