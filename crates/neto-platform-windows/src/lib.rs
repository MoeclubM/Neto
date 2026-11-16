#[cfg(target_os = "windows")]
mod imp {
    use anyhow::{Result, bail};
    use neto_core::PlatformHooks;
    use std::ffi::OsStr;
    use std::net::{SocketAddr, TcpStream};
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::process::CommandExt;
    use std::ptr::{null, null_mut};
    use std::sync::{Arc, Mutex};
    use std::time::Duration;
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_CANCELLED, ERROR_SERVICE_DOES_NOT_EXIST, ERROR_SUCCESS, GetLastError,
        HANDLE, WAIT_OBJECT_0,
    };
    use windows_sys::Win32::Networking::WinInet::{
        INTERNET_OPTION_PER_CONNECTION_OPTION, INTERNET_OPTION_REFRESH,
        INTERNET_OPTION_SETTINGS_CHANGED, INTERNET_PER_CONN_AUTOCONFIG_URL,
        INTERNET_PER_CONN_FLAGS, INTERNET_PER_CONN_OPTION_LISTW, INTERNET_PER_CONN_OPTIONW,
        INTERNET_PER_CONN_OPTIONW_0, INTERNET_PER_CONN_PROXY_BYPASS,
        INTERNET_PER_CONN_PROXY_SERVER, InternetSetOptionW, PROXY_TYPE_AUTO_DETECT,
        PROXY_TYPE_AUTO_PROXY_URL, PROXY_TYPE_DIRECT, PROXY_TYPE_PROXY,
    };
    use windows_sys::Win32::Security::{
        GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation,
    };
    use windows_sys::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, KEY_ALL_ACCESS, KEY_READ, KEY_SET_VALUE, REG_DWORD, REG_SZ,
        RegCloseKey, RegCreateKeyExW, RegDeleteTreeW, RegDeleteValueW, RegOpenKeyExW,
        RegQueryValueExW, RegSetValueExW,
    };
    use windows_sys::Win32::System::Services::{
        CloseServiceHandle, OpenSCManagerW, OpenServiceW, QueryServiceStatus, SC_HANDLE,
        SC_MANAGER_CONNECT, SERVICE_QUERY_STATUS, SERVICE_RUNNING, SERVICE_STATUS,
    };
    use windows_sys::Win32::System::Threading::{
        GetCurrentProcess, GetExitCodeProcess, INFINITE, OpenProcessToken, WaitForSingleObject,
    };
    use windows_sys::Win32::UI::Shell::{
        SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW, ShellExecuteExW,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::SW_HIDE;

    const INTERNET_SETTINGS_KEY: &str =
        r"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const RUN_KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Run";

    #[derive(Default)]
    struct ProxyRegistrySnapshot {
        enable: Option<u32>,
        server: Option<String>,
        bypass: Option<String>,
        auto_config_url: Option<String>,
        auto_detect: Option<u32>,
        owned_endpoint: Option<String>,
    }

    pub struct WindowsPlatformHooks {
        app_id: String,
        active_endpoint: Mutex<Option<String>>,
    }

    impl WindowsPlatformHooks {
        pub fn new(app_id: impl Into<String>) -> Arc<Self> {
            let raw = app_id.into();
            let cleaned = if raw.trim().is_empty() {
                "Neto".to_string()
            } else {
                raw.trim().to_string()
            };
            let hooks = Self {
                app_id: cleaned,
                active_endpoint: Mutex::new(None),
            };
            let _ = restore_or_disable_proxy(&hooks.backup_key_path(), None);
            Arc::new(hooks)
        }

        fn backup_key_path(&self) -> String {
            format!(r"Software\{}\SystemProxy", self.app_id)
        }
    }

    impl PlatformHooks for WindowsPlatformHooks {
        fn app_id(&self) -> &str {
            &self.app_id
        }

        fn set_system_proxy(
            &self,
            host: &str,
            http_port: u16,
            _socks_port: u16,
            bypass: &str,
        ) -> Result<()> {
            let endpoint = format!("{host}:{http_port}");
            backup_current_proxy_if_needed(&self.backup_key_path(), &endpoint)?;
            let server = format!("http={host}:{http_port};https={host}:{http_port}");
            apply_wininet_proxy(&server, bypass)?;
            *self
                .active_endpoint
                .lock()
                .expect("active_endpoint lock poisoned") = Some(endpoint);
            Ok(())
        }

        fn clear_system_proxy(&self) -> Result<()> {
            let owned = self
                .active_endpoint
                .lock()
                .expect("active_endpoint lock poisoned")
                .take();
            restore_or_disable_proxy(&self.backup_key_path(), owned.as_deref())
        }

        fn cleanup_system_proxy_on_exit(
            &self,
            local_ports: &[u16],
            force_clear: bool,
        ) -> Result<()> {
            let owned = self
                .active_endpoint
                .lock()
                .expect("active_endpoint lock poisoned")
                .take();
            let backup_path = self.backup_key_path();
            if force_clear || owned.is_some() || has_reg_subkey(&backup_path) {
                let _ = restore_or_disable_proxy(&backup_path, owned.as_deref());
            }
            let current = read_proxy_snapshot(INTERNET_SETTINGS_KEY);
            if current.enable.unwrap_or(0) != 0 {
                let cur_srv = current.server.as_deref().unwrap_or_default();
                let should_disable = cur_srv.trim().is_empty()
                    || owned
                        .as_deref()
                        .is_some_and(|ep| server_mentions_endpoint(cur_srv, ep))
                    || local_ports.iter().copied().filter(|&p| p > 0).any(|port| {
                        let ep = format!("127.0.0.1:{port}");
                        server_mentions_endpoint(cur_srv, &ep)
                            && (force_clear || !can_connect_loopback(port))
                    });
                if should_disable {
                    disable_wininet_proxy(
                        current.bypass.as_deref(),
                        current.auto_config_url.as_deref(),
                        current.auto_detect,
                    )?;
                }
            }
            Ok(())
        }

        fn set_autostart(&self, enabled: bool) -> Result<()> {
            set_registry_autostart(&self.app_id, enabled)
        }

        fn can_use_tun(&self) -> bool {
            query_tun_service_status(&self.app_id) == "running" || is_process_elevated()
        }

        fn tun_service_status(&self) -> &'static str {
            query_tun_service_status(&self.app_id)
        }

        fn install_tun_service(&self) -> Result<()> {
            run_service_action(&self.app_id, "install")
        }

        fn uninstall_tun_service(&self) -> Result<()> {
            run_service_action(&self.app_id, "uninstall")
        }
    }

    pub fn query_tun_service_status(app_id: &str) -> &'static str {
        let svc_name = wide_null(&neto_core::tun_service::service_name(app_id));
        unsafe {
            let scm: SC_HANDLE = OpenSCManagerW(null(), null(), SC_MANAGER_CONNECT);
            if scm.is_null() {
                return "not_installed";
            }
            let svc: SC_HANDLE = OpenServiceW(scm, svc_name.as_ptr(), SERVICE_QUERY_STATUS);
            if svc.is_null() {
                let err = GetLastError();
                CloseServiceHandle(scm);
                return if err == ERROR_SERVICE_DOES_NOT_EXIST {
                    "not_installed"
                } else {
                    "stopped"
                };
            }
            let mut status: SERVICE_STATUS = std::mem::zeroed();
            let ok = QueryServiceStatus(svc, &mut status);
            CloseServiceHandle(svc);
            CloseServiceHandle(scm);
            if ok != 0 && status.dwCurrentState == SERVICE_RUNNING {
                "running"
            } else {
                "stopped"
            }
        }
    }

    fn service_exe_path() -> Result<std::path::PathBuf> {
        let current = std::env::current_exe()?;
        let dir = current
            .parent()
            .ok_or_else(|| anyhow::anyhow!("无法定位程序所在目录"))?;
        let exe = dir.join("neto-service.exe");
        if !exe.is_file() {
            bail!("未找到 TUN 服务程序：{}", exe.display());
        }
        Ok(exe)
    }

    fn run_service_action(app_id: &str, action: &str) -> Result<()> {
        let exe = service_exe_path()?;
        let safe_id = app_id.replace('"', "");
        if is_process_elevated() {
            let out = std::process::Command::new(&exe)
                .args([action, "--id", &safe_id])
                .creation_flags(0x0800_0000)
                .output()?;
            if !out.status.success() {
                let err = String::from_utf8_lossy(&out.stderr).trim().to_string();
                bail!(if err.is_empty() {
                    format!("服务操作 {action} 失败")
                } else {
                    err
                });
            }
            return Ok(());
        }

        let verb_w = wide_null("runas");
        let file_w = wide_null(&exe.to_string_lossy());
        let params_w = wide_null(&format!("{action} --id \"{safe_id}\""));
        let dir_w = exe.parent().map(|p| wide_null(&p.to_string_lossy()));

        unsafe {
            let mut sei: SHELLEXECUTEINFOW = std::mem::zeroed();
            sei.cbSize = size_of::<SHELLEXECUTEINFOW>() as u32;
            sei.fMask = SEE_MASK_NOCLOSEPROCESS;
            sei.lpVerb = verb_w.as_ptr();
            sei.lpFile = file_w.as_ptr();
            sei.lpParameters = params_w.as_ptr();
            sei.lpDirectory = dir_w.as_ref().map_or(null(), |v| v.as_ptr());
            sei.nShow = SW_HIDE;
            if ShellExecuteExW(&mut sei) == 0 {
                let code = GetLastError();
                if code == ERROR_CANCELLED {
                    bail!("已取消管理员授权");
                }
                bail!("启动提权服务安装器失败 (错误码 {code})");
            }
            if !sei.hProcess.is_null() {
                let wait_res = WaitForSingleObject(sei.hProcess, INFINITE);
                let mut exit_code = 1u32;
                let got_code = GetExitCodeProcess(sei.hProcess, &mut exit_code) != 0;
                CloseHandle(sei.hProcess);
                if wait_res != WAIT_OBJECT_0 || !got_code || exit_code != 0 {
                    bail!("TUN 服务{action}执行失败 (退出码 {exit_code})");
                }
            }
        }
        Ok(())
    }

    pub fn is_process_elevated() -> bool {
        unsafe {
            let mut token: HANDLE = null_mut();
            if OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) == 0 {
                return false;
            }
            let mut elevation = TOKEN_ELEVATION { TokenIsElevated: 0 };
            let mut ret_len = 0u32;
            let ok = GetTokenInformation(
                token,
                TokenElevation,
                &mut elevation as *mut _ as *mut _,
                size_of::<TOKEN_ELEVATION>() as u32,
                &mut ret_len,
            );
            CloseHandle(token);
            ok != 0 && elevation.TokenIsElevated != 0
        }
    }

    fn wide_null(s: &str) -> Vec<u16> {
        OsStr::new(s)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect()
    }

    fn apply_wininet_proxy(server: &str, bypass: &str) -> Result<()> {
        let mut server_w = wide_null(server);
        let mut bypass_w = wide_null(bypass);
        let mut options = [
            INTERNET_PER_CONN_OPTIONW {
                dwOption: INTERNET_PER_CONN_FLAGS,
                Value: INTERNET_PER_CONN_OPTIONW_0 {
                    dwValue: PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY,
                },
            },
            INTERNET_PER_CONN_OPTIONW {
                dwOption: INTERNET_PER_CONN_PROXY_SERVER,
                Value: INTERNET_PER_CONN_OPTIONW_0 {
                    pszValue: server_w.as_mut_ptr(),
                },
            },
            INTERNET_PER_CONN_OPTIONW {
                dwOption: INTERNET_PER_CONN_PROXY_BYPASS,
                Value: INTERNET_PER_CONN_OPTIONW_0 {
                    pszValue: bypass_w.as_mut_ptr(),
                },
            },
        ];
        let mut list = INTERNET_PER_CONN_OPTION_LISTW {
            dwSize: size_of::<INTERNET_PER_CONN_OPTION_LISTW>() as u32,
            pszConnection: null_mut(),
            dwOptionCount: options.len() as u32,
            dwOptionError: 0,
            pOptions: options.as_mut_ptr(),
        };
        unsafe {
            if InternetSetOptionW(
                null(),
                INTERNET_OPTION_PER_CONNECTION_OPTION,
                &mut list as *mut _ as *const _,
                list.dwSize,
            ) == 0
            {
                bail!("WinINet InternetSetOptionW(PER_CONNECTION_OPTION) failed");
            }
            refresh_wininet()?;
        }
        Ok(())
    }

    fn disable_wininet_proxy(
        bypass: Option<&str>,
        pac_url: Option<&str>,
        auto_detect: Option<u32>,
    ) -> Result<()> {
        let mut empty_server_w = wide_null("");
        let mut bypass_w = wide_null(bypass.unwrap_or(""));
        let clean_pac = pac_url.map(str::trim).filter(|s| !s.is_empty());
        let mut pac_w = wide_null(clean_pac.unwrap_or(""));

        let mut flags = PROXY_TYPE_DIRECT;
        if clean_pac.is_some() {
            flags |= PROXY_TYPE_AUTO_PROXY_URL;
        }
        if auto_detect.unwrap_or(0) != 0 {
            flags |= PROXY_TYPE_AUTO_DETECT;
        }

        let mut options = Vec::with_capacity(4);
        options.push(INTERNET_PER_CONN_OPTIONW {
            dwOption: INTERNET_PER_CONN_FLAGS,
            Value: INTERNET_PER_CONN_OPTIONW_0 { dwValue: flags },
        });
        options.push(INTERNET_PER_CONN_OPTIONW {
            dwOption: INTERNET_PER_CONN_PROXY_SERVER,
            Value: INTERNET_PER_CONN_OPTIONW_0 {
                pszValue: empty_server_w.as_mut_ptr(),
            },
        });
        options.push(INTERNET_PER_CONN_OPTIONW {
            dwOption: INTERNET_PER_CONN_PROXY_BYPASS,
            Value: INTERNET_PER_CONN_OPTIONW_0 {
                pszValue: bypass_w.as_mut_ptr(),
            },
        });
        if clean_pac.is_some() {
            options.push(INTERNET_PER_CONN_OPTIONW {
                dwOption: INTERNET_PER_CONN_AUTOCONFIG_URL,
                Value: INTERNET_PER_CONN_OPTIONW_0 {
                    pszValue: pac_w.as_mut_ptr(),
                },
            });
        }

        let mut list = INTERNET_PER_CONN_OPTION_LISTW {
            dwSize: size_of::<INTERNET_PER_CONN_OPTION_LISTW>() as u32,
            pszConnection: null_mut(),
            dwOptionCount: options.len() as u32,
            dwOptionError: 0,
            pOptions: options.as_mut_ptr(),
        };

        unsafe {
            let _ = InternetSetOptionW(
                null(),
                INTERNET_OPTION_PER_CONNECTION_OPTION,
                &mut list as *mut _ as *const _,
                list.dwSize,
            );

            let settings_w = wide_null(INTERNET_SETTINGS_KEY);
            let mut settings_key: HKEY = null_mut();
            if RegCreateKeyExW(
                HKEY_CURRENT_USER,
                settings_w.as_ptr(),
                0,
                null(),
                0,
                KEY_ALL_ACCESS,
                null(),
                &mut settings_key,
                null_mut(),
            ) == ERROR_SUCCESS
            {
                write_reg_dword(settings_key, "ProxyEnable", 0);
                delete_reg_value(settings_key, "ProxyServer");
                if let Some(ov) = bypass.filter(|s| !s.is_empty()) {
                    write_reg_sz(settings_key, "ProxyOverride", ov);
                }
                if let Some(pac) = clean_pac {
                    write_reg_sz(settings_key, "AutoConfigURL", pac);
                } else {
                    delete_reg_value(settings_key, "AutoConfigURL");
                }
                RegCloseKey(settings_key);
            }
            refresh_wininet()?;
        }
        Ok(())
    }

    unsafe fn refresh_wininet() -> Result<()> {
        unsafe {
            if InternetSetOptionW(null(), INTERNET_OPTION_SETTINGS_CHANGED, null(), 0) == 0
                || InternetSetOptionW(null(), INTERNET_OPTION_REFRESH, null(), 0) == 0
            {
                bail!("WinINet InternetSetOptionW(REFRESH) failed");
            }
        }
        Ok(())
    }

    fn parse_proxy_segment_host_port(seg: &str) -> Option<(&str, u16)> {
        let mut s = seg.trim();
        if s.is_empty() {
            return None;
        }
        if let Some((_, rhs)) = s.split_once('=') {
            s = rhs.trim();
        }
        if let Some((_, rhs)) = s.split_once("://") {
            s = rhs.trim();
        }
        s = s.split('/').next().unwrap_or(s).trim();
        if let Some(rest) = s.strip_prefix('[') {
            let (host, after) = rest.split_once(']')?;
            let port_str = after.strip_prefix(':')?;
            let port = port_str.parse::<u16>().ok()?;
            return Some((host, port));
        }
        let (host, port_str) = s.rsplit_once(':')?;
        let port = port_str.parse::<u16>().ok()?;
        Some((host.trim(), port))
    }

    fn is_loopback_host(host: &str) -> bool {
        host.eq_ignore_ascii_case("localhost") || host == "127.0.0.1" || host == "::1"
    }

    fn can_connect_loopback(port: u16) -> bool {
        if port == 0 {
            return false;
        }
        let addr = SocketAddr::from(([127, 0, 0, 1], port));
        TcpStream::connect_timeout(&addr, Duration::from_millis(25)).is_ok()
    }

    fn server_mentions_endpoint(server: &str, endpoint: &str) -> bool {
        let Some((target_host, target_port)) = parse_proxy_segment_host_port(endpoint) else {
            return false;
        };
        let target_is_loopback = is_loopback_host(target_host);
        server
            .split([';', ','])
            .filter_map(parse_proxy_segment_host_port)
            .any(|(h, p)| {
                p == target_port
                    && (h.eq_ignore_ascii_case(target_host)
                        || (target_is_loopback && is_loopback_host(h)))
            })
    }

    fn is_stale_or_owned_proxy(server: &str, owned_endpoint: Option<&str>) -> bool {
        let trimmed = server.trim();
        if trimmed.is_empty() {
            return true;
        }
        if let Some(ep) = owned_endpoint
            && server_mentions_endpoint(trimmed, ep)
        {
            return true;
        }
        let segments: Vec<(&str, u16)> = trimmed
            .split([';', ','])
            .filter_map(parse_proxy_segment_host_port)
            .collect();
        if segments.is_empty() {
            return false;
        }
        segments
            .iter()
            .all(|(h, port)| is_loopback_host(h) && !can_connect_loopback(*port))
    }

    fn has_reg_subkey(subkey: &str) -> bool {
        unsafe {
            let w = wide_null(subkey);
            let mut key: HKEY = null_mut();
            if RegOpenKeyExW(HKEY_CURRENT_USER, w.as_ptr(), 0, KEY_READ, &mut key) == ERROR_SUCCESS
            {
                RegCloseKey(key);
                true
            } else {
                false
            }
        }
    }

    fn read_proxy_snapshot(subkey: &str) -> ProxyRegistrySnapshot {
        let mut snap = ProxyRegistrySnapshot::default();
        unsafe {
            let w = wide_null(subkey);
            let mut key: HKEY = null_mut();
            if RegOpenKeyExW(HKEY_CURRENT_USER, w.as_ptr(), 0, KEY_READ, &mut key) != ERROR_SUCCESS
            {
                return snap;
            }
            snap.enable = read_reg_dword(key, "ProxyEnable");
            snap.server = read_reg_sz(key, "ProxyServer");
            snap.bypass = read_reg_sz(key, "ProxyOverride");
            snap.auto_config_url = read_reg_sz(key, "AutoConfigURL");
            snap.auto_detect = read_reg_dword(key, "AutoDetect");
            snap.owned_endpoint = read_reg_sz(key, "OwnedEndpoint");
            RegCloseKey(key);
        }
        snap
    }

    fn backup_current_proxy_if_needed(backup_subkey: &str, owned_endpoint: &str) -> Result<()> {
        let already_exists = has_reg_subkey(backup_subkey);
        let current = if !already_exists {
            Some(read_proxy_snapshot(INTERNET_SETTINGS_KEY))
        } else {
            None
        };

        unsafe {
            let backup_w = wide_null(backup_subkey);
            let mut backup_key: HKEY = null_mut();
            if RegCreateKeyExW(
                HKEY_CURRENT_USER,
                backup_w.as_ptr(),
                0,
                null(),
                0,
                KEY_ALL_ACCESS,
                null(),
                &mut backup_key,
                null_mut(),
            ) != ERROR_SUCCESS
            {
                bail!("create Windows proxy backup registry key failed");
            }

            if let Some(cur) = current {
                let cur_srv = cur.server.as_deref().unwrap_or_default();
                let keep_external = cur.enable.unwrap_or(0) != 0
                    && !is_stale_or_owned_proxy(cur_srv, Some(owned_endpoint));
                if keep_external {
                    write_reg_dword(backup_key, "ProxyEnable", 1);
                    write_reg_sz(backup_key, "ProxyServer", cur_srv);
                } else {
                    write_reg_dword(backup_key, "ProxyEnable", 0);
                    delete_reg_value(backup_key, "ProxyServer");
                }
                if let Some(ov) = cur.bypass.as_deref().filter(|s| !s.is_empty()) {
                    write_reg_sz(backup_key, "ProxyOverride", ov);
                }
                if let Some(pac) = cur.auto_config_url.as_deref().filter(|s| !s.is_empty()) {
                    write_reg_sz(backup_key, "AutoConfigURL", pac);
                }
                if let Some(ad) = cur.auto_detect {
                    write_reg_dword(backup_key, "AutoDetect", ad);
                }
            }

            write_reg_sz(backup_key, "OwnedEndpoint", owned_endpoint);
            write_reg_dword(backup_key, "BackedUp", 1);
            RegCloseKey(backup_key);
        }
        Ok(())
    }

    fn restore_or_disable_proxy(backup_subkey: &str, owned_mem: Option<&str>) -> Result<()> {
        if has_reg_subkey(backup_subkey) {
            let backed = read_proxy_snapshot(backup_subkey);
            let backup_w = wide_null(backup_subkey);
            unsafe {
                let _ = RegDeleteTreeW(HKEY_CURRENT_USER, backup_w.as_ptr());
            }
            let effective_owned = owned_mem.or(backed.owned_endpoint.as_deref());
            let backed_srv = backed.server.as_deref().unwrap_or_default();
            if backed.enable.unwrap_or(0) != 0
                && !is_stale_or_owned_proxy(backed_srv, effective_owned)
            {
                apply_wininet_proxy(backed_srv, backed.bypass.as_deref().unwrap_or(""))?;
            } else {
                disable_wininet_proxy(
                    backed.bypass.as_deref(),
                    backed.auto_config_url.as_deref(),
                    backed.auto_detect,
                )?;
            }
            return Ok(());
        }

        if let Some(ep) = owned_mem {
            let cur = read_proxy_snapshot(INTERNET_SETTINGS_KEY);
            let cur_srv = cur.server.as_deref().unwrap_or_default();
            if cur.enable.unwrap_or(0) != 0
                && (cur_srv.trim().is_empty() || server_mentions_endpoint(cur_srv, ep))
            {
                disable_wininet_proxy(
                    cur.bypass.as_deref(),
                    cur.auto_config_url.as_deref(),
                    cur.auto_detect,
                )?;
            }
        }
        Ok(())
    }

    unsafe fn read_reg_dword(key: HKEY, name: &str) -> Option<u32> {
        unsafe {
            let nw = wide_null(name);
            let mut kind = 0u32;
            let mut val = 0u32;
            let mut len = 4u32;
            if RegQueryValueExW(
                key,
                nw.as_ptr(),
                null(),
                &mut kind,
                &mut val as *mut _ as *mut u8,
                &mut len,
            ) == ERROR_SUCCESS
                && kind == REG_DWORD
            {
                Some(val)
            } else {
                None
            }
        }
    }

    unsafe fn read_reg_sz(key: HKEY, name: &str) -> Option<String> {
        unsafe {
            let nw = wide_null(name);
            let mut kind = 0u32;
            let mut len = 0u32;
            if RegQueryValueExW(key, nw.as_ptr(), null(), &mut kind, null_mut(), &mut len)
                != ERROR_SUCCESS
                || kind != REG_SZ
                || len < 2
            {
                return None;
            }
            let mut buf = vec![0u8; len as usize];
            if RegQueryValueExW(
                key,
                nw.as_ptr(),
                null(),
                &mut kind,
                buf.as_mut_ptr(),
                &mut len,
            ) != ERROR_SUCCESS
            {
                return None;
            }
            let u16_len = (len as usize) / 2;
            let slice = std::slice::from_raw_parts(buf.as_ptr() as *const u16, u16_len);
            let end = slice.iter().position(|&c| c == 0).unwrap_or(slice.len());
            Some(String::from_utf16_lossy(&slice[..end]))
        }
    }

    unsafe fn write_reg_dword(key: HKEY, name: &str, val: u32) {
        unsafe {
            let nw = wide_null(name);
            let _ = RegSetValueExW(
                key,
                nw.as_ptr(),
                0,
                REG_DWORD,
                &val as *const _ as *const u8,
                4,
            );
        }
    }

    unsafe fn write_reg_sz(key: HKEY, name: &str, val: &str) {
        unsafe {
            let nw = wide_null(name);
            let vw = wide_null(val);
            let _ = RegSetValueExW(
                key,
                nw.as_ptr(),
                0,
                REG_SZ,
                vw.as_ptr() as *const u8,
                (vw.len() * 2) as u32,
            );
        }
    }

    unsafe fn delete_reg_value(key: HKEY, name: &str) {
        unsafe {
            let nw = wide_null(name);
            let _ = RegDeleteValueW(key, nw.as_ptr());
        }
    }

    fn set_registry_autostart(app_id: &str, enabled: bool) -> Result<()> {
        let exe = std::env::current_exe()?;
        let cmd = format!("\"{}\" --minimized", exe.display());
        unsafe {
            let run_w = wide_null(RUN_KEY);
            let mut key: HKEY = null_mut();
            if RegOpenKeyExW(
                HKEY_CURRENT_USER,
                run_w.as_ptr(),
                0,
                KEY_SET_VALUE,
                &mut key,
            ) != ERROR_SUCCESS
            {
                bail!("open HKCU Run registry key failed");
            }
            let name_w = wide_null(app_id);
            if enabled {
                let val_w = wide_null(&cmd);
                let status = RegSetValueExW(
                    key,
                    name_w.as_ptr(),
                    0,
                    REG_SZ,
                    val_w.as_ptr() as *const u8,
                    (val_w.len() * 2) as u32,
                );
                RegCloseKey(key);
                if status != ERROR_SUCCESS {
                    bail!("write autostart registry value failed");
                }
            } else {
                let _ = RegDeleteValueW(key, name_w.as_ptr());
                RegCloseKey(key);
            }
        }
        Ok(())
    }
}

#[cfg(target_os = "windows")]
pub use imp::*;
