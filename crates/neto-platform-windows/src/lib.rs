#[cfg(target_os = "windows")]
mod imp {
    use anyhow::{Result, bail};
    use neto_core::PlatformHooks;
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::{null, null_mut};
    use std::sync::Arc;
    use windows_sys::Win32::Foundation::{CloseHandle, ERROR_SUCCESS, HANDLE};
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
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

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
            is_process_elevated()
        }
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
        OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
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
            if RegOpenKeyExW(HKEY_CURRENT_USER, backup_w.as_ptr(), 0, KEY_READ, &mut existing)
                == ERROR_SUCCESS
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
            if RegOpenKeyExW(HKEY_CURRENT_USER, conn_w.as_ptr(), 0, KEY_READ, &mut conn_key)
                == ERROR_SUCCESS
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
            if RegOpenKeyExW(HKEY_CURRENT_USER, run_w.as_ptr(), 0, KEY_SET_VALUE, &mut key)
                != ERROR_SUCCESS
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
