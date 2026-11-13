#[cfg(target_os = "linux")]
mod imp {
    use anyhow::{Context, Result};
    use ksni::TrayMethods;
    use neto_core::{PlatformHooks, RoutingMode};
    use std::fs::OpenOptions;
    use std::path::PathBuf;
    use std::process::Command;
    use std::sync::Arc;
    use tokio::sync::mpsc;

    #[derive(Debug, Clone)]
    pub enum WaylandTrayAction {
        ShowWindow,
        ToggleConnect,
        SetRoutingMode(RoutingMode),
        ToggleSystemProxy,
        ToggleTun,
        Quit,
    }

    #[derive(Debug, Clone)]
    pub struct WaylandTrayState {
        pub app_id: String,
        pub app_name: String,
        pub connected: bool,
        pub active_node: String,
        pub routing_mode: RoutingMode,
        pub system_proxy_enabled: bool,
        pub tun_enabled: bool,
    }

    pub struct LinuxWaylandPlatformHooks {
        app_id: String,
        app_name: String,
    }

    impl LinuxWaylandPlatformHooks {
        pub fn new(app_id: impl Into<String>, app_name: impl Into<String>) -> Arc<Self> {
            Arc::new(Self {
                app_id: app_id.into(),
                app_name: app_name.into(),
            })
        }
    }

    impl PlatformHooks for LinuxWaylandPlatformHooks {
        fn set_system_proxy(
            &self,
            host: &str,
            http_port: u16,
            socks_port: u16,
            _bypass: &str,
        ) -> Result<()> {
            let mut applied = false;
            if has_binary("gsettings") {
                let _ = run_cmd("gsettings", &["set", "org.gnome.system.proxy", "mode", "manual"]);
                let _ = run_cmd("gsettings", &["set", "org.gnome.system.proxy.http", "host", host]);
                let _ = run_cmd(
                    "gsettings",
                    &["set", "org.gnome.system.proxy.http", "port", &http_port.to_string()],
                );
                let _ = run_cmd(
                    "gsettings",
                    &["set", "org.gnome.system.proxy.https", "host", host],
                );
                let _ = run_cmd(
                    "gsettings",
                    &["set", "org.gnome.system.proxy.https", "port", &http_port.to_string()],
                );
                let _ = run_cmd(
                    "gsettings",
                    &["set", "org.gnome.system.proxy.socks", "host", host],
                );
                let _ = run_cmd(
                    "gsettings",
                    &[
                        "set",
                        "org.gnome.system.proxy.socks",
                        "port",
                        &socks_port.to_string(),
                    ],
                );
                applied = true;
            }
            for kwrite in ["kwriteconfig6", "kwriteconfig5"] {
                if has_binary(kwrite) {
                    let http_url = format!("http://{host}:{http_port}");
                    let socks_url = format!("socks://{host}:{socks_port}");
                    let _ = run_cmd(
                        kwrite,
                        &["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "ProxyType", "1"],
                    );
                    let _ = run_cmd(
                        kwrite,
                        &["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "httpProxy", &http_url],
                    );
                    let _ = run_cmd(
                        kwrite,
                        &["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "httpsProxy", &http_url],
                    );
                    let _ = run_cmd(
                        kwrite,
                        &["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "socksProxy", &socks_url],
                    );
                    notify_kio_reload();
                    applied = true;
                    break;
                }
            }
            if !applied {
                log::warn!("neither gsettings nor kwriteconfig found; system proxy kept at local port");
            }
            Ok(())
        }

        fn clear_system_proxy(&self) -> Result<()> {
            if has_binary("gsettings") {
                let _ = run_cmd("gsettings", &["set", "org.gnome.system.proxy", "mode", "none"]);
            }
            for kwrite in ["kwriteconfig6", "kwriteconfig5"] {
                if has_binary(kwrite) {
                    let _ = run_cmd(
                        kwrite,
                        &["--file", "kioslaverc", "--group", "Proxy Settings", "--key", "ProxyType", "0"],
                    );
                    notify_kio_reload();
                    break;
                }
            }
            Ok(())
        }

        fn set_autostart(&self, enabled: bool) -> Result<()> {
            let autostart_dir = xdg_config_home().join("autostart");
            std::fs::create_dir_all(&autostart_dir)?;
            let desktop_file = autostart_dir.join(format!("{}.desktop", self.app_id));
            if enabled {
                let exe = std::env::current_exe().context("resolve current executable")?;
                let icon = if self.app_id.trim().is_empty() {
                    "network-vpn"
                } else {
                    self.app_id.as_str()
                };
                let content = format!(
                    "[Desktop Entry]\nType=Application\nName={}\nExec=\"{}\" --minimized\nIcon={}\nTerminal=false\nCategories=Network;\nStartupWMClass={}\nX-GNOME-Autostart-enabled=true\n",
                    self.app_name,
                    exe.display(),
                    icon,
                    self.app_id
                );
                std::fs::write(&desktop_file, content)?;
            } else if desktop_file.exists() {
                let _ = std::fs::remove_file(&desktop_file);
            }
            Ok(())
        }

        fn can_use_tun(&self) -> bool {
            OpenOptions::new()
                .read(true)
                .write(true)
                .open("/dev/net/tun")
                .is_ok()
        }
    }

    pub fn is_wayland_session() -> bool {
        std::env::var_os("WAYLAND_DISPLAY").is_some()
            || std::env::var("XDG_SESSION_TYPE")
                .map(|v| v.eq_ignore_ascii_case("wayland"))
                .unwrap_or(false)
    }

    pub fn prefer_wayland_backend() {
        if is_wayland_session() && std::env::var_os("GDK_BACKEND").is_none() {
            unsafe {
                std::env::set_var("GDK_BACKEND", "wayland,x11");
            }
        }
    }

    struct SniTray {
        state: WaylandTrayState,
        tx: mpsc::UnboundedSender<WaylandTrayAction>,
    }

    impl ksni::Tray for SniTray {
        fn id(&self) -> String {
            self.state.app_id.clone()
        }

        fn title(&self) -> String {
            if self.state.connected {
                if self.state.active_node.is_empty() {
                    format!("{} · 已连接", self.state.app_name)
                } else {
                    format!("{} · {}", self.state.app_name, self.state.active_node)
                }
            } else {
                self.state.app_name.clone()
            }
        }

        fn icon_name(&self) -> String {
            if self.state.connected {
                "network-vpn-symbolic".to_string()
            } else {
                "network-offline-symbolic".to_string()
            }
        }

        fn activate(&mut self, _x: i32, _y: i32) {
            let _ = self.tx.send(WaylandTrayAction::ShowWindow);
        }

        fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
            use ksni::menu::*;
            vec![
                StandardItem {
                    label: "打开窗口".into(),
                    activate: Box::new(|this: &mut Self| {
                        let _ = this.tx.send(WaylandTrayAction::ShowWindow);
                    }),
                    ..Default::default()
                }
                .into(),
                StandardItem {
                    label: if self.state.connected { "断开连接".into() } else { "立即连接".into() },
                    activate: Box::new(|this: &mut Self| {
                        let _ = this.tx.send(WaylandTrayAction::ToggleConnect);
                    }),
                    ..Default::default()
                }
                .into(),
                MenuItem::Separator,
                CheckmarkItem {
                    label: "系统代理".into(),
                    checked: self.state.system_proxy_enabled,
                    activate: Box::new(|this: &mut Self| {
                        let _ = this.tx.send(WaylandTrayAction::ToggleSystemProxy);
                    }),
                    ..Default::default()
                }
                .into(),
                CheckmarkItem {
                    label: "TUN 模式".into(),
                    checked: self.state.tun_enabled,
                    activate: Box::new(|this: &mut Self| {
                        let _ = this.tx.send(WaylandTrayAction::ToggleTun);
                    }),
                    ..Default::default()
                }
                .into(),
                SubMenu {
                    label: "出站模式".into(),
                    submenu: vec![
                        CheckmarkItem {
                            label: "规则".into(),
                            checked: self.state.routing_mode == RoutingMode::Rule,
                            activate: Box::new(|this: &mut Self| {
                                let _ = this.tx.send(WaylandTrayAction::SetRoutingMode(RoutingMode::Rule));
                            }),
                            ..Default::default()
                        }
                        .into(),
                        CheckmarkItem {
                            label: "全局".into(),
                            checked: self.state.routing_mode == RoutingMode::Global,
                            activate: Box::new(|this: &mut Self| {
                                let _ = this.tx.send(WaylandTrayAction::SetRoutingMode(RoutingMode::Global));
                            }),
                            ..Default::default()
                        }
                        .into(),
                        CheckmarkItem {
                            label: "直连".into(),
                            checked: self.state.routing_mode == RoutingMode::Direct,
                            activate: Box::new(|this: &mut Self| {
                                let _ = this.tx.send(WaylandTrayAction::SetRoutingMode(RoutingMode::Direct));
                            }),
                            ..Default::default()
                        }
                        .into(),
                    ],
                    ..Default::default()
                }
                .into(),
                MenuItem::Separator,
                StandardItem {
                    label: "退出".into(),
                    icon_name: "application-exit".into(),
                    activate: Box::new(|this: &mut Self| {
                        let _ = this.tx.send(WaylandTrayAction::Quit);
                    }),
                    ..Default::default()
                }
                .into(),
            ]
        }
    }

    #[derive(Clone)]
    pub struct WaylandTrayHandle {
        handle: ksni::Handle<SniTray>,
    }

    impl WaylandTrayHandle {
        pub async fn spawn(
            initial: WaylandTrayState,
        ) -> Result<(Self, mpsc::UnboundedReceiver<WaylandTrayAction>)> {
            let (tx, rx) = mpsc::unbounded_channel();
            let tray = SniTray { state: initial, tx };
            let handle = tray
                .assume_sni_available(true)
                .spawn()
                .await
                .map_err(|e| anyhow::anyhow!("spawn StatusNotifierItem tray failed: {e}"))?;
            Ok((Self { handle }, rx))
        }

        pub async fn update(&self, next: WaylandTrayState) {
            let _ = self
                .handle
                .update(|tray| {
                    tray.state = next;
                })
                .await;
        }
    }

    fn xdg_config_home() -> PathBuf {
        std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")))
            .unwrap_or_else(|| PathBuf::from("/tmp"))
    }

    fn has_binary(name: &str) -> bool {
        Command::new("sh")
            .args(["-c", &format!("command -v {name} >/dev/null 2>&1")])
            .status()
            .map(|s| s.success())
            .unwrap_or(false)
    }

    fn run_cmd(bin: &str, args: &[&str]) -> Result<()> {
        let status = Command::new(bin).args(args).status()?;
        anyhow::ensure!(status.success(), "{bin} exited with {status}");
        Ok(())
    }

    fn notify_kio_reload() {
        if has_binary("dbus-send") {
            let _ = Command::new("dbus-send")
                .args([
                    "--type=signal",
                    "/KIO/Scheduler",
                    "org.kde.KIO.Scheduler.reparseSlaveConfiguration",
                    "string:",
                ])
                .status();
        }
    }
}

#[cfg(target_os = "linux")]
pub use imp::*;
