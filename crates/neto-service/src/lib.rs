#[cfg(not(windows))]
pub fn run_cli() -> anyhow::Result<()> {
    Ok(())
}

#[cfg(windows)]
mod imp {
    use aerion::{TunCancellationToken, run_tun};
    use anyhow::{Context, Result, bail};
    use neto_core::tun_service::{
        SERVICE_VERSION, ServiceRequest, TunReply, pipe_name, service_name, write_line,
    };
    use std::ffi::{OsStr, OsString};
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::null_mut;
    use std::sync::{Arc, OnceLock};
    use std::time::{Duration, Instant};
    use tokio::io::{AsyncBufReadExt, BufReader};
    use tokio::net::windows::named_pipe::{NamedPipeServer, ServerOptions};
    use tokio::sync::{Mutex, watch};
    use windows_service::service::{
        ServiceAccess, ServiceControl, ServiceControlAccept, ServiceErrorControl, ServiceExitCode,
        ServiceInfo, ServiceStartType, ServiceState, ServiceStatus, ServiceType,
    };
    use windows_service::service_control_handler::{self, ServiceControlHandlerResult};
    use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};
    use windows_service::{define_windows_service, service_dispatcher};
    use windows_sys::Win32::Foundation::{ERROR_SERVICE_DOES_NOT_EXIST, HLOCAL, LocalFree};
    use windows_sys::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW;
    use windows_sys::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};

    static SERVICE_APP_ID: OnceLock<String> = OnceLock::new();

    define_windows_service!(ffi_service_main, service_entry);

    struct ActiveTunSlot {
        shutdown: TunCancellationToken,
        done: watch::Receiver<bool>,
    }

    pub fn run_cli() -> Result<()> {
        let args: Vec<String> = std::env::args().skip(1).collect();
        let mut app_id = "Neto".to_string();
        let mut cmd = String::new();
        let mut i = 0usize;
        while i < args.len() {
            match args[i].as_str() {
                "--id" => {
                    if let Some(val) = args.get(i + 1) {
                        app_id = val.clone();
                        i += 1;
                    }
                }
                "--serve" | "serve" => cmd = "serve".to_string(),
                "install" | "uninstall" | "stop" if cmd.is_empty() => {
                    cmd = args[i].clone();
                }
                _ => {}
            }
            i += 1;
        }
        if cmd.is_empty() {
            cmd = "serve".to_string();
        }
        match cmd.as_str() {
            "install" => install_service(&app_id),
            "uninstall" => uninstall_service(&app_id),
            "stop" => stop_service(&app_id),
            _ => {
                let _ = SERVICE_APP_ID.set(app_id.clone());
                service_dispatcher::start(service_name(&app_id), ffi_service_main)
                    .context("start Windows TUN service dispatcher")
            }
        }
    }

    fn parse_id_from_os(arguments: &[OsString]) -> Option<String> {
        let list: Vec<String> = arguments
            .iter()
            .map(|s| s.to_string_lossy().into_owned())
            .collect();
        for idx in 0..list.len() {
            if list[idx] == "--id"
                && let Some(val) = list.get(idx + 1)
            {
                return Some(val.clone());
            }
        }
        None
    }

    fn build_service_info(app_id: &str) -> Result<ServiceInfo> {
        let exe = std::env::current_exe().context("resolve neto-service.exe path")?;
        Ok(ServiceInfo {
            name: OsString::from(service_name(app_id)),
            display_name: OsString::from(format!("{app_id} TUN Service")),
            service_type: ServiceType::OWN_PROCESS,
            start_type: ServiceStartType::AutoStart,
            error_control: ServiceErrorControl::Normal,
            executable_path: exe,
            launch_arguments: vec![
                OsString::from("--serve"),
                OsString::from("--id"),
                OsString::from(app_id),
            ],
            dependencies: Vec::new(),
            account_name: None,
            account_password: None,
        })
    }

    fn is_not_exist_error(err: &windows_service::Error) -> bool {
        matches!(
            err,
            windows_service::Error::Winapi(io_err)
                if io_err.raw_os_error() == Some(ERROR_SERVICE_DOES_NOT_EXIST as i32)
        )
    }

    fn wait_for_state(
        service: &windows_service::service::Service,
        target: ServiceState,
        timeout: Duration,
    ) -> Result<()> {
        let deadline = Instant::now() + timeout;
        loop {
            let status = service.query_status()?;
            if status.current_state == target {
                return Ok(());
            }
            if Instant::now() >= deadline {
                bail!("等待服务状态 {:?} 超时", target);
            }
            std::thread::sleep(Duration::from_millis(150));
        }
    }

    fn install_service(app_id: &str) -> Result<()> {
        let manager = ServiceManager::local_computer(
            None::<&str>,
            ServiceManagerAccess::CONNECT | ServiceManagerAccess::CREATE_SERVICE,
        )
        .context("打开 Windows 服务管理器失败")?;
        let svc_name = service_name(app_id);
        let info = build_service_info(app_id)?;

        let service = match manager.open_service(&svc_name, ServiceAccess::ALL_ACCESS) {
            Ok(svc) => {
                if let Ok(st) = svc.query_status()
                    && st.current_state != ServiceState::Stopped
                {
                    let _ = svc.stop();
                    let _ = wait_for_state(&svc, ServiceState::Stopped, Duration::from_secs(10));
                }
                svc.change_config(&info)
                    .context("更新 TUN 服务配置失败")?;
                svc
            }
            Err(err) if is_not_exist_error(&err) => manager
                .create_service(&info, ServiceAccess::ALL_ACCESS)
                .context("注册 TUN 服务失败")?,
            Err(err) => return Err(err).context("查询已有 TUN 服务失败"),
        };

        let _ = service.set_description(format!("{app_id} Windows TUN 代理系统服务"));
        let st = service.query_status()?;
        if st.current_state != ServiceState::Running {
            service
                .start(&[] as &[&OsStr])
                .context("启动 TUN 服务失败")?;
            wait_for_state(&service, ServiceState::Running, Duration::from_secs(10))?;
        }
        Ok(())
    }

    fn stop_service(app_id: &str) -> Result<()> {
        let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)?;
        let svc_name = service_name(app_id);
        let service = match manager.open_service(
            &svc_name,
            ServiceAccess::QUERY_STATUS | ServiceAccess::STOP,
        ) {
            Ok(svc) => svc,
            Err(err) if is_not_exist_error(&err) => return Ok(()),
            Err(err) => return Err(err.into()),
        };
        if let Ok(st) = service.query_status()
            && st.current_state != ServiceState::Stopped
        {
            let _ = service.stop();
            wait_for_state(&service, ServiceState::Stopped, Duration::from_secs(10))?;
        }
        Ok(())
    }

    fn uninstall_service(app_id: &str) -> Result<()> {
        let manager = ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)?;
        let svc_name = service_name(app_id);
        let service = match manager.open_service(
            &svc_name,
            ServiceAccess::QUERY_STATUS | ServiceAccess::STOP | ServiceAccess::DELETE,
        ) {
            Ok(svc) => svc,
            Err(err) if is_not_exist_error(&err) => return Ok(()),
            Err(err) => return Err(err.into()),
        };
        if let Ok(st) = service.query_status()
            && st.current_state != ServiceState::Stopped
        {
            let _ = service.stop();
            let _ = wait_for_state(&service, ServiceState::Stopped, Duration::from_secs(10));
        }
        service.delete().context("删除 TUN 服务失败")?;
        Ok(())
    }

    fn service_entry(arguments: Vec<OsString>) {
        let app_id = parse_id_from_os(&arguments)
            .or_else(|| SERVICE_APP_ID.get().cloned())
            .unwrap_or_else(|| "Neto".to_string());
        let _ = run_service(&app_id);
    }

    fn create_pipe_server(pipe: &str, first: bool) -> std::io::Result<NamedPipeServer> {
        let sddl: Vec<u16> = OsStr::new("D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;AU)")
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let mut sd: PSECURITY_DESCRIPTOR = null_mut();
        let ok = unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                1,
                &mut sd,
                null_mut(),
            )
        };
        let mut sa = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: if ok != 0 { sd } else { null_mut() },
            bInheritHandle: 0,
        };
        let mut opts = ServerOptions::new();
        opts.first_pipe_instance(first);
        let res = unsafe {
            opts.create_with_security_attributes_raw(
                pipe,
                if ok != 0 {
                    (&mut sa as *mut SECURITY_ATTRIBUTES).cast()
                } else {
                    null_mut()
                },
            )
        };
        if !sd.is_null() {
            unsafe {
                LocalFree(sd as HLOCAL);
            }
        }
        res
    }

    async fn stop_active_tun(slot: &Arc<Mutex<Option<ActiveTunSlot>>>) {
        let prev = slot.lock().await.take();
        if let Some(mut active) = prev {
            active.shutdown.cancel();
            let _ = tokio::time::timeout(Duration::from_secs(5), async {
                while !*active.done.borrow() {
                    if active.done.changed().await.is_err() {
                        break;
                    }
                }
            })
            .await;
        }
    }

    async fn handle_client(
        stream: NamedPipeServer,
        active_slot: Arc<Mutex<Option<ActiveTunSlot>>>,
        service_shutdown: TunCancellationToken,
    ) {
        let (r, mut w) = tokio::io::split(stream);
        let mut lines = BufReader::new(r).lines();
        let Ok(Some(first_line)) = lines.next_line().await else {
            return;
        };
        let req: ServiceRequest = match serde_json::from_str(&first_line) {
            Ok(req) => req,
            Err(err) => {
                let _ = write_line(
                    &mut w,
                    &TunReply {
                        ok: false,
                        version: Some(SERVICE_VERSION.to_string()),
                        error: Some(format!("无效请求: {err}")),
                    },
                )
                .await;
                return;
            }
        };

        match req {
            ServiceRequest::Ping => {
                let _ = write_line(
                    &mut w,
                    &TunReply {
                        ok: true,
                        version: Some(SERVICE_VERSION.to_string()),
                        error: None,
                    },
                )
                .await;
            }
            ServiceRequest::Start { spec } => {
                let tun_config = match spec.to_config() {
                    Ok(cfg) => cfg,
                    Err(err) => {
                        let _ = write_line(
                            &mut w,
                            &TunReply {
                                ok: false,
                                version: Some(SERVICE_VERSION.to_string()),
                                error: Some(format!("{err:#}")),
                            },
                        )
                        .await;
                        return;
                    }
                };

                stop_active_tun(&active_slot).await;

                let shutdown = TunCancellationToken::new();
                let (done_tx, mut done_rx) = watch::channel(false);
                let (err_tx, mut err_rx) = tokio::sync::oneshot::channel::<Option<String>>();
                let run_shutdown = shutdown.clone();
                tokio::spawn(async move {
                    let res = run_tun(tun_config, run_shutdown).await;
                    let err_msg = res.err().map(|e| format!("{e:#}"));
                    let _ = err_tx.send(err_msg);
                    let _ = done_tx.send(true);
                });

                tokio::select! {
                    early = &mut err_rx => {
                        let msg = early.ok().flatten().unwrap_or_else(|| "TUN 运行提前退出".to_string());
                        let _ = write_line(
                            &mut w,
                            &TunReply {
                                ok: false,
                                version: Some(SERVICE_VERSION.to_string()),
                                error: Some(msg),
                            },
                        )
                        .await;
                        return;
                    }
                    _ = tokio::time::sleep(Duration::from_millis(160)) => {}
                }

                *active_slot.lock().await = Some(ActiveTunSlot {
                    shutdown: shutdown.clone(),
                    done: done_rx.clone(),
                });

                if write_line(
                    &mut w,
                    &TunReply {
                        ok: true,
                        version: Some(SERVICE_VERSION.to_string()),
                        error: None,
                    },
                )
                .await
                .is_err()
                {
                    shutdown.cancel();
                    let _ = tokio::time::timeout(Duration::from_secs(5), async {
                        while !*done_rx.borrow() {
                            if done_rx.changed().await.is_err() {
                                break;
                            }
                        }
                    })
                    .await;
                    return;
                }

                tokio::select! {
                    _ = service_shutdown.cancelled() => {
                        shutdown.cancel();
                    }
                    early = &mut err_rx => {
                        let msg = early.ok().flatten();
                        let _ = write_line(
                            &mut w,
                            &TunReply {
                                ok: false,
                                version: Some(SERVICE_VERSION.to_string()),
                                error: msg,
                            },
                        )
                        .await;
                        return;
                    }
                    _ = lines.next_line() => {
                        shutdown.cancel();
                    }
                }

                let _ = tokio::time::timeout(Duration::from_secs(5), async {
                    while !*done_rx.borrow() {
                        if done_rx.changed().await.is_err() {
                            break;
                        }
                    }
                })
                .await;
                let _ = write_line(
                    &mut w,
                    &TunReply {
                        ok: true,
                        version: Some(SERVICE_VERSION.to_string()),
                        error: None,
                    },
                )
                .await;
            }
        }
    }

    fn run_service(app_id: &str) -> Result<()> {
        if let Ok(exe) = std::env::current_exe()
            && let Some(dir) = exe.parent()
        {
            let _ = std::env::set_current_dir(dir);
        }
        let svc_name = service_name(app_id);
        let pipe = pipe_name(app_id);
        let shutdown = TunCancellationToken::new();
        let handler_shutdown = shutdown.clone();

        let status_handle =
            service_control_handler::register(&svc_name, move |control| match control {
                ServiceControl::Stop | ServiceControl::Shutdown => {
                    handler_shutdown.cancel();
                    ServiceControlHandlerResult::NoError
                }
                ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
                _ => ServiceControlHandlerResult::NotImplemented,
            })?;

        status_handle.set_service_status(ServiceStatus {
            service_type: ServiceType::OWN_PROCESS,
            current_state: ServiceState::Running,
            controls_accepted: ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
            exit_code: ServiceExitCode::Win32(0),
            checkpoint: 0,
            wait_hint: Duration::default(),
            process_id: None,
        })?;

        let rt = tokio::runtime::Runtime::new()?;
        let active_slot: Arc<Mutex<Option<ActiveTunSlot>>> = Arc::new(Mutex::new(None));
        rt.block_on(async {
            let mut server = match create_pipe_server(&pipe, true) {
                Ok(s) => s,
                Err(_) => return,
            };
            loop {
                tokio::select! {
                    _ = shutdown.cancelled() => {
                        stop_active_tun(&active_slot).await;
                        break;
                    }
                    res = server.connect() => {
                        if res.is_err() {
                            break;
                        }
                        let connected = server;
                        match create_pipe_server(&pipe, false) {
                            Ok(next) => server = next,
                            Err(_) => {
                                let slot = active_slot.clone();
                                let sd = shutdown.clone();
                                tokio::spawn(handle_client(connected, slot, sd));
                                break;
                            }
                        }
                        let slot = active_slot.clone();
                        let sd = shutdown.clone();
                        tokio::spawn(handle_client(connected, slot, sd));
                    }
                }
            }
        });

        let _ = status_handle.set_service_status(ServiceStatus {
            service_type: ServiceType::OWN_PROCESS,
            current_state: ServiceState::Stopped,
            controls_accepted: ServiceControlAccept::empty(),
            exit_code: ServiceExitCode::Win32(0),
            checkpoint: 0,
            wait_hint: Duration::default(),
            process_id: None,
        });
        Ok(())
    }
}

#[cfg(windows)]
pub fn run_cli() -> anyhow::Result<()> {
    imp::run_cli()
}
