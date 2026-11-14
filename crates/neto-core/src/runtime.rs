use crate::config_compat::node_to_proxy_config;
use crate::connections::{RouteMatchFn, spawn_tracked_socks_ingress, static_route_matcher};
use crate::http_proxy::connect_via_socks5;
use crate::protocol::{AerionProxyConfig, spawn_aerion_listener};
use crate::telemetry;
use anyhow::{Context, Result, bail, ensure};
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex as StdMutex};
use std::time::Instant;
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::task::JoinHandle;
use tokio::time::{Duration, timeout};

type Callback = Box<dyn Fn(String, String) + Send + Sync>;
static LOG_CALLBACK: Lazy<StdMutex<Option<Callback>>> = Lazy::new(|| StdMutex::new(None));
static EVENT_CALLBACK: Lazy<StdMutex<Option<Callback>>> = Lazy::new(|| StdMutex::new(None));

pub fn set_log_callback<F>(f: F)
where
    F: Fn(String, String) + Send + Sync + 'static,
{
    *LOG_CALLBACK.lock().unwrap() = Some(Box::new(f));
}

pub fn set_event_callback<F>(f: F)
where
    F: Fn(String, String) + Send + Sync + 'static,
{
    *EVENT_CALLBACK.lock().unwrap() = Some(Box::new(f));
}

pub(crate) fn on_log(level: &str, message: &str) {
    telemetry::push_log(level, message);
    if let Some(cb) = LOG_CALLBACK.lock().unwrap().as_ref() {
        cb(level.to_string(), message.to_string());
    }
}

pub(crate) fn on_event(event_json: &str) {
    if let Some(cb) = EVENT_CALLBACK.lock().unwrap().as_ref() {
        cb("event".to_string(), event_json.to_string());
    }
}

pub(crate) fn core_event_json(
    event: &aerion::CoreEvent,
    wrapper_session_id: Option<u64>,
) -> String {
    let mut value = match event {
        aerion::CoreEvent::UsersReplaced { user_ids } => json!({
            "type": "users_replaced",
            "user_ids": user_ids,
        }),
        aerion::CoreEvent::SessionOpened {
            user_id,
            session_id,
            source_ip,
        } => json!({
            "type": "session_opened",
            "user_id": user_id,
            "session_id": session_id,
            "source_ip": source_ip,
        }),
        aerion::CoreEvent::SessionClosed {
            user_id,
            session_id,
            source_ip,
        } => json!({
            "type": "session_closed",
            "user_id": user_id,
            "session_id": session_id,
            "source_ip": source_ip,
        }),
        aerion::CoreEvent::SessionCancelled {
            user_id,
            session_id,
            source_ip,
        } => json!({
            "type": "session_cancelled",
            "user_id": user_id,
            "session_id": session_id,
            "source_ip": source_ip,
        }),
        aerion::CoreEvent::TrafficRecorded {
            user_id,
            session_id,
            direction,
            bytes,
            upload_bytes,
            download_bytes,
        } => json!({
            "type": "traffic_recorded",
            "user_id": user_id,
            "session_id": session_id,
            "direction": match direction {
                aerion::TrafficDirection::Upload => "upload",
                aerion::TrafficDirection::Download => "download",
            },
            "bytes": bytes,
            "upload_bytes": upload_bytes,
            "download_bytes": download_bytes,
        }),
    };
    if let (Some(id), Value::Object(object)) = (wrapper_session_id, &mut value) {
        object.insert("wrapper_session_id".to_string(), json!(id));
    }
    value.to_string()
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StartVpnRequest {
    #[serde(default)]
    pub node: Value,
    #[serde(default)]
    pub route: Option<crate::route::StartRouteRequest>,
    #[serde(default)]
    pub pipe_name: Option<String>,
    pub tun_fd: Option<i32>,
    pub mtu: u16,
    pub dns: String,
    pub dns_addr: String,
    pub virtual_dns_pool: String,
    pub bypass: Option<Vec<String>>,
    pub ipv6: bool,
    pub tcp_timeout_secs: Option<u64>,
    pub udp_timeout_secs: Option<u64>,
    pub max_sessions: Option<usize>,
    pub exit_on_fatal_error: Option<bool>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TestNodeRequest {
    pub node: Value,
    pub target_host: String,
    pub target_port: u16,
    pub target_tls: bool,
    pub timeout_ms: u64,
}

#[derive(Deserialize)]
struct StartSocksRequest {
    node: Value,
}

struct SocksSession {
    _ingress_task: JoinHandle<()>,
    _task: JoinHandle<Result<()>>,
    _log_task: Option<JoinHandle<()>>,
    _event_task: Option<JoinHandle<()>>,
    _core: Option<aerion::ProxyCore>,
}

fn matcher_for_node(node: &Value) -> RouteMatchFn {
    let proto = node
        .get("type")
        .or_else(|| node.get("protocol"))
        .and_then(Value::as_str)
        .unwrap_or_default();
    if proto.eq_ignore_ascii_case("direct") {
        return static_route_matcher("DIRECT", "DIRECT");
    }
    let name = node
        .get("name")
        .and_then(Value::as_str)
        .filter(|s| !s.trim().is_empty())
        .unwrap_or("PROXY");
    static_route_matcher("GLOBAL", name)
}

static NEXT_SOCKS_SESSION_ID: AtomicU64 = AtomicU64::new(1);
static SOCKS_SESSIONS: Lazy<StdMutex<HashMap<u64, SocksSession>>> =
    Lazy::new(|| StdMutex::new(HashMap::new()));

async fn stop_listener(task: JoinHandle<Result<()>>) {
    task.abort();
    if let Err(error) = task.await
        && !error.is_cancelled()
    {
        on_log("error", &format!("Aerion listener join failed: {error}"));
    }
}

fn host_tun_bypass_cidrs(host: &str, include_ipv6: bool) -> Vec<String> {
    let trimmed = host.trim().trim_matches(['[', ']']);
    let Ok(ip) = trimmed.parse::<IpAddr>() else {
        return Vec::new();
    };
    if ip.is_loopback() || ip.is_unspecified() {
        return Vec::new();
    }
    match ip {
        IpAddr::V4(v4) => vec![format!("{v4}/32")],
        IpAddr::V6(v6) if include_ipv6 => vec![format!("{v6}/128")],
        IpAddr::V6(_) => Vec::new(),
    }
}

pub(crate) fn flux_tun_bypass_cidrs(
    config: &aerion::FluxClientConfig,
    include_ipv6: bool,
) -> Vec<String> {
    let mut out = Vec::new();
    for ip in config.all_static_ips() {
        if ip.is_loopback() || ip.is_unspecified() {
            continue;
        }
        match ip {
            IpAddr::V4(v4) => {
                let cidr = format!("{v4}/32");
                if !out.contains(&cidr) {
                    out.push(cidr);
                }
            }
            IpAddr::V6(v6) if include_ipv6 => {
                let cidr = format!("{v6}/128");
                if !out.contains(&cidr) {
                    out.push(cidr);
                }
            }
            IpAddr::V6(_) => {}
        }
    }
    out
}

pub(crate) fn proxy_tun_bypass_cidrs(
    config: &AerionProxyConfig,
    include_ipv6: bool,
) -> Vec<String> {
    match config {
        AerionProxyConfig::Flux(cfg) => flux_tun_bypass_cidrs(cfg, include_ipv6),
        AerionProxyConfig::AnyTls(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::HttpProxy(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Hysteria2(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Trojan(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Vless(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Vmess(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Mieru(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Sudoku(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Naive(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Shadowsocks(cfg) => {
            host_tun_bypass_cidrs(&cfg.server_host, include_ipv6)
        }
        AerionProxyConfig::SocksProxy(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Tuic(cfg) => host_tun_bypass_cidrs(&cfg.server_host, include_ipv6),
        AerionProxyConfig::Route(_) => Vec::new(),
    }
}

pub(crate) async fn start_aerion_socks(
    node: Value,
    track_traffic: bool,
) -> Result<(
    SocketAddr,
    JoinHandle<Result<()>>,
    Option<aerion::ProxyCore>,
    Vec<String>,
)> {
    let listen = SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 0);
    let listener = TcpListener::bind(listen)
        .await
        .context("bind Aerion local SOCKS listener")?;
    let local_addr = listener.local_addr().context("read Aerion SOCKS address")?;
    let config = node_to_proxy_config(&node, local_addr)?;
    let bypass_cidrs = proxy_tun_bypass_cidrs(&config, !cfg!(target_os = "windows"));
    let core = if track_traffic {
        match &config {
            AerionProxyConfig::AnyTls(c) => {
                Some(aerion::ProxyCore::from_credentials(&c.password, &[]))
            }
            AerionProxyConfig::Flux(c) => Some(aerion::ProxyCore::from_credentials(&c.key, &[])),
            AerionProxyConfig::Trojan(c) => {
                Some(aerion::ProxyCore::from_credentials(&c.password, &[]))
            }
            AerionProxyConfig::Vless(c) => {
                Some(aerion::ProxyCore::from_credentials(&c.user_id, &[]))
            }
            AerionProxyConfig::Vmess(c) => {
                Some(aerion::ProxyCore::from_credentials(&c.user_id, &[]))
            }
            AerionProxyConfig::Hysteria2(c) => {
                Some(aerion::ProxyCore::from_credentials(&c.password, &[]))
            }
            AerionProxyConfig::Mieru(c) => {
                let cred = if c.username.is_empty() {
                    c.password.as_str()
                } else {
                    c.username.as_str()
                };
                Some(aerion::ProxyCore::from_credentials(cred, &[]))
            }
            AerionProxyConfig::Sudoku(c) => Some(aerion::ProxyCore::from_credentials(&c.key, &[])),
            AerionProxyConfig::Naive(c) => Some(aerion::ProxyCore::from_credentials(
                &format!("{}:{}", c.username, c.password),
                &[],
            )),
            AerionProxyConfig::Shadowsocks(c) => {
                Some(aerion::ProxyCore::from_credentials(&c.password, &[]))
            }
            AerionProxyConfig::Tuic(c) => Some(aerion::ProxyCore::from_credentials(&c.uuid, &[])),
            AerionProxyConfig::Route(_) => Some(aerion::ProxyCore::from_credentials("direct", &[])),
            _ => None,
        }
    } else {
        None
    };
    let task = spawn_aerion_listener(listener, config, core.clone());
    Ok((local_addr, task, core, bypass_cidrs))
}

pub async fn start_socks_from_json(input: &str) -> Result<String> {
    let request: StartSocksRequest =
        serde_json::from_str(input).context("parse start SOCKS request")?;
    let log_bridge = aerion::LogBridge::new();
    let session_id = NEXT_SOCKS_SESSION_ID.fetch_add(1, Ordering::SeqCst);
    let matcher = matcher_for_node(&request.node);
    let (inner_socks, task, core, _) = start_aerion_socks(request.node, true).await?;
    let (socks_addr, ingress_task) = spawn_tracked_socks_ingress(inner_socks, matcher).await?;

    let log_task = {
        let mut rx = log_bridge.subscribe();
        tokio::spawn(async move {
            while let Some(entry) = rx.recv().await {
                on_log(&entry.level.to_string(), &entry.message);
            }
        })
    };
    let event_task = core.as_ref().map(|core| {
        let mut rx = core.subscribe_events();
        tokio::spawn(async move {
            while let Some(event) = rx.recv().await {
                telemetry::record_core_event(&event);
                on_event(&core_event_json(&event, Some(session_id)));
            }
        })
    });

    SOCKS_SESSIONS
        .lock()
        .expect("SOCKS session map lock poisoned")
        .insert(
            session_id,
            SocksSession {
                _ingress_task: ingress_task,
                _task: task,
                _log_task: Some(log_task),
                _event_task: event_task,
                _core: core,
            },
        );

    Ok(json!({
        "ok": true,
        "session_id": session_id,
        "socks_addr": socks_addr.to_string(),
    })
    .to_string())
}

pub async fn stop_socks(session_id: u64) -> Result<String> {
    let session = SOCKS_SESSIONS
        .lock()
        .expect("SOCKS session map lock poisoned")
        .remove(&session_id)
        .with_context(|| format!("SOCKS session not found: {session_id}"))?;
    if let Some(core) = session._core {
        core.cancel_all_sessions();
    }
    session._ingress_task.abort();
    session._task.abort();
    if let Some(task) = session._log_task {
        task.abort();
    }
    if let Some(task) = session._event_task {
        task.abort();
    }
    Ok(json!({"ok": true, "session_id": session_id}).to_string())
}

pub async fn test_node(request: TestNodeRequest) -> Result<u64> {
    ensure!(
        !request.target_host.trim().is_empty(),
        "node test target_host is required"
    );
    let target_host = request.target_host.trim().to_string();
    let timeout_duration = Duration::from_millis(request.timeout_ms.max(1000));
    let (socks_addr, mut task, _, _) = start_aerion_socks(request.node, false).await?;
    let result = timeout(timeout_duration, async {
        probe_via_socks(
            socks_addr,
            &target_host,
            request.target_port,
            request.target_tls,
        )
        .await
    })
    .await;
    match result {
        Ok(Ok(latency)) => {
            stop_listener(task).await;
            Ok(latency)
        }
        Ok(Err(error)) => {
            let listener_err = finished_listener_error(&mut task).await;
            stop_listener(task).await;
            if let Some(le) = listener_err {
                bail!("{le}: Aerion SOCKS listener exited during node test");
            }
            Err(error)
        }
        Err(elapsed) => {
            let listener_err = finished_listener_error(&mut task).await;
            stop_listener(task).await;
            if let Some(le) = listener_err {
                bail!("{le}: Aerion SOCKS listener exited during node test");
            }
            bail!("node test timed out: {elapsed}")
        }
    }
}

pub async fn test_node_from_json(input: &str) -> Result<String> {
    let request: TestNodeRequest =
        serde_json::from_str(input).context("parse node test request")?;
    let target_host = request.target_host.clone();
    let target_port = request.target_port;
    let target_tls = request.target_tls;
    match test_node(request).await {
        Ok(latency) => Ok(json!({
            "ok": true,
            "latency_ms": latency,
            "first_latency_ms": latency,
            "target_host": target_host,
            "target_port": target_port,
            "target_tls": target_tls,
        })
        .to_string()),
        Err(error) => Ok(json!({
            "ok": false,
            "error": error.to_string(),
        })
        .to_string()),
    }
}

pub async fn probe_via_socks(
    socks_addr: SocketAddr,
    target_host: &str,
    target_port: u16,
    target_tls: bool,
) -> Result<u64> {
    let mut stream = connect_via_socks5(socks_addr, target_host, target_port).await?;
    let host_header = if (target_tls && target_port == 443) || (!target_tls && target_port == 80) {
        target_host.to_string()
    } else {
        format!("{target_host}:{target_port}")
    };
    if target_tls {
        let mut roots = rustls::RootCertStore::empty();
        roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
        let config = rustls::ClientConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_safe_default_protocol_versions()?
        .with_root_certificates(roots)
        .with_no_client_auth();
        let server_name = rustls::pki_types::ServerName::try_from(target_host.to_string())
            .with_context(|| format!("invalid TLS test target: {target_host}"))?;
        let mut tls = tokio_rustls::TlsConnector::from(Arc::new(config))
            .connect(server_name, stream)
            .await
            .context("connect TLS test target through Aerion")?;
        return send_http_probe(&mut tls, &host_header).await;
    }
    send_http_probe(&mut stream, &host_header).await
}

async fn finished_listener_error(task: &mut JoinHandle<Result<()>>) -> Option<anyhow::Error> {
    if !task.is_finished() {
        return None;
    }
    match task.await {
        Ok(Ok(())) => Some(anyhow::anyhow!("Aerion SOCKS listener exited")),
        Ok(Err(error)) => Some(error),
        Err(error) if error.is_cancelled() => None,
        Err(error) => Some(anyhow::anyhow!(
            "Aerion SOCKS listener join failed: {error}"
        )),
    }
}

async fn send_http_probe<S>(stream: &mut S, host_header: &str) -> Result<u64>
where
    S: AsyncRead + AsyncWrite + Unpin,
{
    let request = format!("HEAD / HTTP/1.1\r\nHost: {host_header}\r\nConnection: close\r\n\r\n");
    let started = Instant::now();
    stream
        .write_all(request.as_bytes())
        .await
        .context("write HTTP probe request")?;
    let mut response = Vec::new();
    let mut buffer = [0u8; 1024];
    loop {
        let read = stream
            .read(&mut buffer)
            .await
            .context("read HTTP probe response")?;
        ensure!(read > 0, "target closed before HTTP response");
        response.extend_from_slice(&buffer[..read]);
        let prefix_len = response.len().min(5);
        ensure!(
            response[..prefix_len] == b"HTTP/"[..prefix_len],
            "target response is not HTTP"
        );
        if response.windows(4).any(|window| window == b"\r\n\r\n") {
            return Ok(started.elapsed().as_millis() as u64);
        }
        ensure!(
            response.len() < 4096,
            "target HTTP response header is too large"
        );
    }
}

#[cfg(target_os = "android")]
mod platform {
    use super::*;
    use aerion::{
        TunCancellationToken, TunConfig, TunDnsStrategy, TunVerbosity, socks_proxy_url, spawn_tun,
    };

    static NEXT_VPN_SESSION_ID: AtomicU64 = AtomicU64::new(1);
    static VPN_SESSIONS: Lazy<StdMutex<HashMap<u64, VpnSession>>> =
        Lazy::new(|| StdMutex::new(HashMap::new()));

    struct VpnSession {
        shutdown: TunCancellationToken,
        proxy_tasks: Vec<JoinHandle<Result<()>>>,
        ingress_task: JoinHandle<()>,
        _core: Option<aerion::ProxyCore>,
    }

    pub async fn start(input: &str) -> Result<String> {
        let request: StartVpnRequest =
            serde_json::from_str(input).context("parse start VPN request")?;
        let mtu = request.mtu;
        let dns_name = request.dns.clone();
        let dns = match dns_name.as_str() {
            "virtual" => TunDnsStrategy::Virtual,
            "direct" => TunDnsStrategy::Direct,
            "over_tcp" => TunDnsStrategy::OverTcp,
            other => bail!("unsupported VPN DNS strategy: {other}"),
        };
        let dns_addr: IpAddr = request.dns_addr.parse().context("parse VPN DNS address")?;
        let tun_fd = request
            .tun_fd
            .ok_or_else(|| anyhow::anyhow!("tun_fd is required on Android"))?;

        let (socks_addr, proxy_tasks, ingress_task, core) = match request.route {
            Some(route_req) => {
                let rt = crate::route::start_route_runtime(route_req).await?;
                let mut tasks = rt.outbound_tasks;
                tasks.push(rt.router_task);
                (rt.socks_addr, tasks, rt.ingress_task, Some(rt.core))
            }
            None => {
                let matcher = matcher_for_node(&request.node);
                let (inner, task, core, _) = start_aerion_socks(request.node, true).await?;
                let (tracked, itask) = spawn_tracked_socks_ingress(inner, matcher).await?;
                (tracked, vec![task], itask, core)
            }
        };

        let mut tun_config = TunConfig::new(socks_proxy_url(socks_addr));
        tun_config.tun_fd = Some(tun_fd);
        tun_config.close_fd_on_drop = false;
        tun_config.setup = false;
        tun_config.mtu = mtu;
        tun_config.packet_information = false;
        tun_config.dns = dns;
        tun_config.dns_addr = dns_addr;
        tun_config.virtual_dns_pool = request.virtual_dns_pool;
        if let Some(bypass) = request.bypass {
            tun_config.bypass = bypass;
        }
        tun_config.ipv6 = request.ipv6;
        if let Some(tcp_timeout_secs) = request.tcp_timeout_secs {
            tun_config.tcp_timeout_secs = tcp_timeout_secs;
        }
        if let Some(udp_timeout_secs) = request.udp_timeout_secs {
            tun_config.udp_timeout_secs = udp_timeout_secs;
        }
        if let Some(max_sessions) = request.max_sessions {
            tun_config.max_sessions = max_sessions;
        }
        if let Some(exit_on_fatal_error) = request.exit_on_fatal_error {
            tun_config.exit_on_fatal_error = exit_on_fatal_error;
        }
        let virtual_dns_pool = tun_config.virtual_dns_pool.clone();
        tun_config.verbosity = TunVerbosity::Info;

        let log_bridge = aerion::LogBridge::new();
        let runtime = spawn_tun(tun_config).context("spawn Aerion TUN runtime")?;
        let shutdown = runtime.shutdown_token();
        let session_id = NEXT_VPN_SESSION_ID.fetch_add(1, Ordering::SeqCst);
        VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .insert(
                session_id,
                VpnSession {
                    shutdown,
                    proxy_tasks,
                    ingress_task,
                    _core: core.clone(),
                },
            );

        let log_task_inner = {
            let mut rx = log_bridge.subscribe();
            tokio::spawn(async move {
                while let Some(entry) = rx.recv().await {
                    on_log(&entry.level.to_string(), &entry.message);
                }
            })
        };

        let event_task_inner = core.as_ref().map(|core| {
            let mut event_rx = core.subscribe_events();
            tokio::spawn(async move {
                while let Some(event) = event_rx.recv().await {
                    telemetry::record_core_event(&event);
                    on_event(&core_event_json(&event, Some(session_id)));
                }
            })
        });

        tokio::spawn(async move {
            if let Err(error) = runtime.wait().await {
                log::error!("Aerion TUN runtime exited with error: {error:?}");
            }
            if let Some(session) = VPN_SESSIONS
                .lock()
                .expect("VPN session map lock poisoned")
                .remove(&session_id)
            {
                if let Some(core) = session._core {
                    core.cancel_all_sessions();
                }
                session.ingress_task.abort();
                for t in session.proxy_tasks {
                    t.abort();
                }
                log_task_inner.abort();
                if let Some(task) = event_task_inner {
                    task.abort();
                }
            }
        });

        Ok(json!({
            "ok": true,
            "session_id": session_id,
            "mtu": mtu,
            "dns": dns_name,
            "dns_addr": dns_addr.to_string(),
            "virtual_dns_pool": virtual_dns_pool,
            "socks_addr": socks_addr.to_string(),
        })
        .to_string())
    }

    pub async fn stop(session_id: u64) -> Result<String> {
        let session = VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .remove(&session_id)
            .with_context(|| format!("VPN session not found: {session_id}"))?;
        session.shutdown.cancel();
        if let Some(core) = session._core {
            core.cancel_all_sessions();
        }
        session.ingress_task.abort();
        for t in session.proxy_tasks {
            t.abort();
        }
        Ok(json!({"ok": true, "session_id": session_id}).to_string())
    }
}

#[cfg(any(target_os = "windows", target_os = "linux"))]
mod platform {
    use super::*;
    use aerion::{
        TunCancellationToken, TunConfig, TunDnsStrategy, TunVerbosity, socks_proxy_url, spawn_tun,
    };

    static NEXT_VPN_SESSION_ID: AtomicU64 = AtomicU64::new(1);
    static VPN_SESSIONS: Lazy<StdMutex<HashMap<u64, VpnSession>>> =
        Lazy::new(|| StdMutex::new(HashMap::new()));

    struct VpnSession {
        shutdown: TunCancellationToken,
        finished: tokio::sync::oneshot::Receiver<std::result::Result<(), String>>,
        proxy_tasks: Vec<JoinHandle<Result<()>>>,
        ingress_task: JoinHandle<()>,
        _log_task: Option<JoinHandle<()>>,
        _event_task: Option<JoinHandle<()>>,
        _core: Option<aerion::ProxyCore>,
    }

    enum TunWaitHandle {
        Local(aerion::TunRuntime),
        Remote(JoinHandle<Result<()>>),
    }

    fn abort_tasks(tasks: Vec<JoinHandle<Result<()>>>) {
        for task in tasks {
            task.abort();
        }
    }

    fn clear_outbound_if_idle() {
        if VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .is_empty()
        {
            aerion::clear_outbound_interface();
        }
    }

    pub async fn start(input: &str) -> Result<String> {
        let request: StartVpnRequest =
            serde_json::from_str(input).context("parse start VPN request")?;
        let mtu = request.mtu;
        let dns_name = request.dns.clone();
        let dns = match dns_name.as_str() {
            "virtual" => TunDnsStrategy::Virtual,
            "direct" => TunDnsStrategy::Direct,
            "over_tcp" => TunDnsStrategy::OverTcp,
            other => bail!("unsupported VPN DNS strategy: {other}"),
        };
        let dns_addr: IpAddr = request.dns_addr.parse().context("parse VPN DNS address")?;

        if VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .is_empty()
            && let Some(iface) = aerion::detect_default_outbound_interface()
        {
            aerion::set_outbound_interface(iface);
        }

        let (socks_addr, proxy_tasks, ingress_task, core, bypass_cidrs) = match request.route {
            Some(route_req) => match crate::route::start_route_runtime(route_req).await {
                Ok(rt) => {
                    let mut tasks = rt.outbound_tasks;
                    tasks.push(rt.router_task);
                    (
                        rt.socks_addr,
                        tasks,
                        rt.ingress_task,
                        Some(rt.core),
                        rt.bypass_cidrs,
                    )
                }
                Err(error) => {
                    clear_outbound_if_idle();
                    return Err(error);
                }
            },
            None => {
                let matcher = matcher_for_node(&request.node);
                match start_aerion_socks(request.node, true).await {
                    Ok((inner, task, core, cidrs)) => {
                        match spawn_tracked_socks_ingress(inner, matcher).await {
                            Ok((tracked, itask)) => (tracked, vec![task], itask, core, cidrs),
                            Err(error) => {
                                task.abort();
                                clear_outbound_if_idle();
                                return Err(error);
                            }
                        }
                    }
                    Err(error) => {
                        clear_outbound_if_idle();
                        return Err(error);
                    }
                }
            }
        };

        let mut tun_config = TunConfig::new(socks_proxy_url(socks_addr));
        tun_config.setup = true;
        tun_config.mtu = mtu;
        tun_config.packet_information = false;
        tun_config.dns = dns;
        tun_config.dns_addr = dns_addr;
        tun_config.virtual_dns_pool = request.virtual_dns_pool;
        if let Some(bypass) = request.bypass {
            tun_config.bypass = bypass;
        }
        for cidr in bypass_cidrs {
            if !tun_config.bypass.contains(&cidr) {
                tun_config.bypass.push(cidr);
            }
        }
        tun_config.ipv6 = request.ipv6;
        if let Some(tcp_timeout_secs) = request.tcp_timeout_secs {
            tun_config.tcp_timeout_secs = tcp_timeout_secs;
        }
        tun_config.udp_timeout_secs = request.udp_timeout_secs.unwrap_or(60);
        tun_config.max_sessions = request.max_sessions.unwrap_or(2048);
        if let Some(exit_on_fatal_error) = request.exit_on_fatal_error {
            tun_config.exit_on_fatal_error = exit_on_fatal_error;
        }
        let virtual_dns_pool = tun_config.virtual_dns_pool.clone();
        tun_config.verbosity = TunVerbosity::Info;

        let log_bridge = aerion::LogBridge::new();
        let remote_pipe = request
            .pipe_name
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty());
        let (shutdown, wait_handle) = if cfg!(target_os = "windows") && remote_pipe.is_some() {
            #[cfg(target_os = "windows")]
            {
                let spec = crate::tun_service::TunSpec {
                    socks_addr,
                    mtu,
                    dns: dns_name.clone(),
                    dns_addr,
                    virtual_dns_pool: virtual_dns_pool.clone(),
                    bypass: tun_config.bypass.clone(),
                    ipv6: tun_config.ipv6,
                    tcp_timeout_secs: request.tcp_timeout_secs,
                    udp_timeout_secs: tun_config.udp_timeout_secs,
                    max_sessions: tun_config.max_sessions,
                };
                match crate::tun_service::start_remote_tun(remote_pipe.unwrap(), &spec).await {
                    Ok((token, task)) => (token, TunWaitHandle::Remote(task)),
                    Err(error) => {
                        if let Some(core) = core {
                            core.cancel_all_sessions();
                        }
                        ingress_task.abort();
                        abort_tasks(proxy_tasks);
                        clear_outbound_if_idle();
                        return Err(error);
                    }
                }
            }
            #[cfg(not(target_os = "windows"))]
            unreachable!()
        } else {
            match spawn_tun(tun_config) {
                Ok(runtime) => (runtime.shutdown_token(), TunWaitHandle::Local(runtime)),
                Err(error) => {
                    if let Some(core) = core {
                        core.cancel_all_sessions();
                    }
                    ingress_task.abort();
                    abort_tasks(proxy_tasks);
                    clear_outbound_if_idle();
                    return Err(error).context("spawn Aerion TUN runtime");
                }
            }
        };
        let (finished_tx, finished_rx) = tokio::sync::oneshot::channel();
        let session_id = NEXT_VPN_SESSION_ID.fetch_add(1, Ordering::SeqCst);

        let log_task = {
            let mut rx = log_bridge.subscribe();
            Some(tokio::spawn(async move {
                while let Some(entry) = rx.recv().await {
                    on_log(&entry.level.to_string(), &entry.message);
                }
            }))
        };

        let event_task = core.as_ref().map(|core| {
            let mut event_rx = core.subscribe_events();
            tokio::spawn(async move {
                while let Some(event) = event_rx.recv().await {
                    telemetry::record_core_event(&event);
                    on_event(&core_event_json(&event, Some(session_id)));
                }
            })
        });

        VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .insert(
                session_id,
                VpnSession {
                    shutdown,
                    finished: finished_rx,
                    proxy_tasks,
                    ingress_task,
                    _log_task: log_task,
                    _event_task: event_task,
                    _core: core,
                },
            );

        tokio::spawn(async move {
            let result = match wait_handle {
                TunWaitHandle::Local(runtime) => runtime
                    .wait()
                    .await
                    .map(|_| ())
                    .map_err(|error| format!("{error:#}")),
                TunWaitHandle::Remote(task) => match task.await {
                    Ok(Ok(())) => Ok(()),
                    Ok(Err(error)) => Err(format!("{error:#}")),
                    Err(error) => Err(format!("{error:#}")),
                },
            };
            if let Some(session) = VPN_SESSIONS
                .lock()
                .expect("VPN session map lock poisoned")
                .remove(&session_id)
            {
                if let Some(core) = session._core {
                    core.cancel_all_sessions();
                }
                session.ingress_task.abort();
                abort_tasks(session.proxy_tasks);
                if let Some(task) = session._log_task {
                    task.abort();
                }
                if let Some(task) = session._event_task {
                    task.abort();
                }
                clear_outbound_if_idle();
                on_event(
                    &json!({
                        "type": "vpn_stopped",
                        "wrapper_session_id": session_id,
                        "error": result.as_ref().err(),
                    })
                    .to_string(),
                );
            }
            let _ = finished_tx.send(result);
        });

        Ok(json!({
            "ok": true,
            "session_id": session_id,
            "mtu": mtu,
            "dns": dns_name,
            "dns_addr": dns_addr.to_string(),
            "virtual_dns_pool": virtual_dns_pool,
            "socks_addr": socks_addr.to_string(),
        })
        .to_string())
    }

    pub async fn stop(session_id: u64) -> Result<String> {
        let session = VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .remove(&session_id)
            .with_context(|| format!("VPN session not found: {session_id}"))?;
        session.shutdown.cancel();
        if let Some(core) = session._core {
            core.cancel_all_sessions();
        }
        session.ingress_task.abort();
        abort_tasks(session.proxy_tasks);
        if let Some(task) = session._log_task {
            task.abort();
        }
        if let Some(task) = session._event_task {
            task.abort();
        }
        clear_outbound_if_idle();
        session
            .finished
            .await
            .context("wait for TUN shutdown")?
            .map_err(anyhow::Error::msg)?;
        Ok(json!({"ok": true, "session_id": session_id}).to_string())
    }

    pub async fn shutdown() -> Result<()> {
        let session_ids: Vec<_> = VPN_SESSIONS
            .lock()
            .expect("VPN session map lock poisoned")
            .keys()
            .copied()
            .collect();
        for session_id in session_ids {
            stop(session_id).await?;
        }
        Ok(())
    }
}

#[cfg(not(any(target_os = "android", target_os = "windows", target_os = "linux")))]
mod platform {
    use super::*;

    pub async fn start(input: &str) -> Result<String> {
        let _request: StartVpnRequest =
            serde_json::from_str(input).context("parse start VPN request")?;
        bail!("VPN is not supported on this platform")
    }

    pub async fn stop(_session_id: u64) -> Result<String> {
        bail!("VPN is not supported on this platform")
    }
}

pub async fn start_vpn_from_json(input: &str) -> Result<String> {
    platform::start(input).await
}

pub async fn stop_vpn(session_id: u64) -> Result<String> {
    platform::stop(session_id).await
}

#[cfg(any(target_os = "windows", target_os = "linux"))]
pub async fn shutdown_vpns() -> Result<()> {
    platform::shutdown().await
}
