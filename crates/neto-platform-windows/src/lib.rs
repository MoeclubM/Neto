#[cfg(target_os = "windows")]
mod imp {
    use anyhow::{Result, bail};
    use neto_core::PlatformHooks;
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::process::CommandExt;
    use std::ptr::{null, null_mut};
    use std::sync::Arc;
    use windows_sys::Win32::Foundation::{
        CloseHandle, ERROR_CANCELLED, ERROR_SERVICE_DOES_NOT_EXIST, ERROR_SUCCESS, GetLastError,
        HANDLE, WAIT_OBJECT_0,
    };
    use windows_sys::Win32::Networking::WinInet::{
        INTERNET_OPTION_PER_CONNECTION_OPTION, INTERNET_OPTION_REFRESH,
        INTERNET_OPTION_SETTINGS_CHANGED, INTERNET_PER_CONN_FLAGS, INTERNET_PER_CONN_OPTION_LISTW,
        INTERNET_PER_CONN_OPTIONW, INTERNET_PER_CONN_OPTIONW_0, INTERNET_PER_CONN_PROXY_BYPASS,
        INTERNET_PER_CONN_PROXY_SERVER, InternetSetOptionW, PROXY_TYPE_DIRECT, PROXY_TYPE_PROXY,
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
    const BACKUP_VALUE_NAMES: &[&str] = &[
        "ProxyEnable",
        "ProxyServer",
        "ProxyOverride",
        "AutoConfigURL",
        "AutoDetect",
    ];
    const CONNECTION_VALUE_NAMES: &[&str] = &["DefaultConnectionSettings", "SavedLegacySettings"];

    pub struct WindowsPlatformHooks {
        app_id: String,
    }

    impl WindowsPlatformHooks {
        pub fn new(app_id: impl Into<String>) -> Arc<Self> {
            let raw = app_id.into();
            let cleaned = if raw.trim().is_empty() {
                "Neto".to_string()
            } else {
                raw.trim().to_string()
            };
            let hooks = Self { app_id: cleaned };
            let _ = restore_backed_up_proxy(&hooks.backup_key_path());
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
            backup_current_proxy_if_needed(&self.backup_key_path())?;
            let server = format!("http={host}:{http_port};https={host}:{http_port}");
            apply_wininet_proxy(&server, bypass)?;
            Ok(())
        }

        fn clear_system_proxy(&self) -> Result<()> {
            restore_backed_up_proxy(&self.backup_key_path())
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

    fn backup_current_proxy_if_needed(backup_subkey: &str) -> Result<()> {
        unsafe {
            let backup_w = wide_null(backup_subkey);
            let mut existing: HKEY = null_mut();
            if RegOpenKeyExW(
                HKEY_CURRENT_USER,
                backup_w.as_ptr(),
                0,
                KEY_READ,
                &mut existing,
            ) == ERROR_SUCCESS
            {
                RegCloseKey(existing);
                return Ok(());
            }
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

            let settings_w = wide_null(INTERNET_SETTINGS_KEY);
            let mut settings_key: HKEY = null_mut();
            if RegOpenKeyExW(
                HKEY_CURRENT_USER,
                settings_w.as_ptr(),
                0,
                KEY_READ,
                &mut settings_key,
            ) == ERROR_SUCCESS
            {
                for name in BACKUP_VALUE_NAMES {
                    copy_reg_value(settings_key, name, backup_key, name);
                }
                RegCloseKey(settings_key);
            }

            let conn_w = wide_null(&format!(r"{INTERNET_SETTINGS_KEY}\Connections"));
            let mut conn_key: HKEY = null_mut();
            if RegOpenKeyExW(
                HKEY_CURRENT_USER,
                conn_w.as_ptr(),
                0,
                KEY_READ,
                &mut conn_key,
            ) == ERROR_SUCCESS
            {
                for name in CONNECTION_VALUE_NAMES {
                    copy_reg_value(conn_key, name, backup_key, &format!("Connection.{name}"));
                }
                RegCloseKey(conn_key);
            }
            let marker_name = wide_null("BackedUp");
            let marker_val = 1u32;
            let _ = RegSetValueExW(
                backup_key,
                marker_name.as_ptr(),
                0,
                REG_DWORD,
                &marker_val as *const _ as *const u8,
                4,
            );
            RegCloseKey(backup_key);
        }
        Ok(())
    }

    fn restore_backed_up_proxy(backup_subkey: &str) -> Result<()> {
        unsafe {
            let backup_w = wide_null(backup_subkey);
            let mut backup_key: HKEY = null_mut();
            if RegOpenKeyExW(
                HKEY_CURRENT_USER,
                backup_w.as_ptr(),
                0,
                KEY_READ,
                &mut backup_key,
            ) != ERROR_SUCCESS
            {
                return Ok(());
            }
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
                for name in BACKUP_VALUE_NAMES {
                    if !copy_reg_value(backup_key, name, settings_key, name) {
                        let nw = wide_null(name);
                        let _ = RegDeleteValueW(settings_key, nw.as_ptr());
                    }
                }
                RegCloseKey(settings_key);
            }

            let conn_w = wide_null(&format!(r"{INTERNET_SETTINGS_KEY}\Connections"));
            let mut conn_key: HKEY = null_mut();
            if RegCreateKeyExW(
                HKEY_CURRENT_USER,
                conn_w.as_ptr(),
                0,
                null(),
                0,
                KEY_ALL_ACCESS,
                null(),
                &mut conn_key,
                null_mut(),
            ) == ERROR_SUCCESS
            {
                for name in CONNECTION_VALUE_NAMES {
                    if !copy_reg_value(backup_key, &format!("Connection.{name}"), conn_key, name) {
                        let nw = wide_null(name);
                        let _ = RegDeleteValueW(conn_key, nw.as_ptr());
                    }
                }
                RegCloseKey(conn_key);
            }

            RegCloseKey(backup_key);
            let _ = RegDeleteTreeW(HKEY_CURRENT_USER, backup_w.as_ptr());
            refresh_wininet()?;
        }
        Ok(())
    }

    unsafe fn copy_reg_value(src: HKEY, src_name: &str, dst: HKEY, dst_name: &str) -> bool {
        unsafe {
            let src_w = wide_null(src_name);
            let mut kind = 0u32;
            let mut len = 0u32;
            if RegQueryValueExW(src, src_w.as_ptr(), null(), &mut kind, null_mut(), &mut len)
                != ERROR_SUCCESS
            {
                return false;
            }
            let mut buf = vec![0u8; len as usize];
            if RegQueryValueExW(
                src,
                src_w.as_ptr(),
                null(),
                &mut kind,
                buf.as_mut_ptr(),
                &mut len,
            ) != ERROR_SUCCESS
            {
                return false;
            }
            let dst_w = wide_null(dst_name);
            RegSetValueExW(dst, dst_w.as_ptr(), 0, kind, buf.as_ptr(), len) == ERROR_SUCCESS
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
