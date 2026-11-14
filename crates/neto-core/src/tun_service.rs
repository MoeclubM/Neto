use aerion::{TunCancellationToken, TunConfig, TunDnsStrategy, TunVerbosity};
use anyhow::{Context, Result, bail, ensure};
use serde::{Deserialize, Serialize};
use std::net::{IpAddr, SocketAddr};
use tokio::io::{AsyncBufReadExt, AsyncRead, AsyncWrite, AsyncWriteExt, BufReader};
use tokio::task::JoinHandle;

pub const SERVICE_VERSION: &str = env!("CARGO_PKG_VERSION");

pub fn sanitize_app_id(app_id: &str) -> String {
    let clean: String = app_id
        .trim()
        .chars()
        .filter(|c| c.is_ascii_alphanumeric() || *c == '-' || *c == '_')
        .collect();
    if clean.is_empty() {
        let mut hex = String::with_capacity(app_id.len() * 2);
        for b in app_id.as_bytes() {
            hex.push_str(&format!("{b:02x}"));
        }
        if hex.is_empty() {
            "Neto".to_string()
        } else {
            format!("App{hex}")
        }
    } else {
        clean
    }
}

pub fn service_name(app_id: &str) -> String {
    format!("{}TunService", sanitize_app_id(app_id))
}

pub fn pipe_name(app_id: &str) -> String {
    format!(r"\\.\pipe\{}", service_name(app_id))
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TunSpec {
    pub socks_addr: SocketAddr,
    pub mtu: u16,
    pub dns: String,
    pub dns_addr: IpAddr,
    pub virtual_dns_pool: String,
    pub bypass: Vec<String>,
    pub ipv6: bool,
    pub tcp_timeout_secs: Option<u64>,
    pub udp_timeout_secs: u64,
    pub max_sessions: usize,
}

impl TunSpec {
    pub fn to_config(&self) -> Result<TunConfig> {
        ensure!(
            self.socks_addr.ip().is_loopback(),
            "TUN 上游必须是本机回环地址"
        );
        ensure!(
            (1280..=9000).contains(&self.mtu),
            "无效的 TUN MTU: {}",
            self.mtu
        );
        let mut cfg = TunConfig::new(aerion::socks_proxy_url(self.socks_addr));
        cfg.setup = true;
        cfg.mtu = self.mtu;
        cfg.packet_information = false;
        cfg.dns = match self.dns.as_str() {
            "virtual" => TunDnsStrategy::Virtual,
            "direct" => TunDnsStrategy::Direct,
            "over_tcp" => TunDnsStrategy::OverTcp,
            other => bail!("unsupported VPN DNS strategy: {other}"),
        };
        cfg.dns_addr = self.dns_addr;
        cfg.virtual_dns_pool = self.virtual_dns_pool.clone();
        cfg.bypass = self.bypass.clone();
        cfg.ipv6 = self.ipv6;
        if let Some(secs) = self.tcp_timeout_secs {
            cfg.tcp_timeout_secs = secs;
        }
        cfg.udp_timeout_secs = self.udp_timeout_secs;
        cfg.max_sessions = self.max_sessions;
        cfg.exit_on_fatal_error = false;
        cfg.verbosity = TunVerbosity::Info;
        Ok(cfg)
    }
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(tag = "cmd", rename_all = "snake_case")]
pub enum ServiceRequest {
    Ping,
    Start { spec: TunSpec },
}

#[derive(Debug, Serialize, Deserialize)]
pub struct TunReply {
    pub ok: bool,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub error: Option<String>,
}

pub async fn write_line<W: AsyncWrite + Unpin>(w: &mut W, value: &impl Serialize) -> Result<()> {
    let mut line = serde_json::to_vec(value)?;
    line.push(b'\n');
    w.write_all(&line).await?;
    w.flush().await?;
    Ok(())
}

#[cfg(windows)]
pub async fn start_remote_tun(
    pipe: &str,
    spec: &TunSpec,
) -> Result<(TunCancellationToken, JoinHandle<Result<()>>)> {
    use tokio::net::windows::named_pipe::ClientOptions;
    let mut attempts = 0usize;
    let client = loop {
        match ClientOptions::new().open(pipe) {
            Ok(c) => break c,
            Err(e) if e.raw_os_error() == Some(231) && attempts < 40 => {
                attempts += 1;
                tokio::time::sleep(std::time::Duration::from_millis(50)).await;
            }
            Err(e) => return Err(e).context("连接 TUN 服务失败，请在设置中安装或重启 TUN 服务"),
        }
    };
    start_with_stream(client, spec).await
}

pub async fn start_with_stream<S: AsyncRead + AsyncWrite + Send + 'static>(
    stream: S,
    spec: &TunSpec,
) -> Result<(TunCancellationToken, JoinHandle<Result<()>>)> {
    let (r, mut w) = tokio::io::split(stream);
    let mut lines = BufReader::new(r).lines();
    write_line(&mut w, &ServiceRequest::Start { spec: spec.clone() }).await?;
    let reply_line = lines.next_line().await?.context("TUN 服务未响应")?;
    let reply: TunReply = serde_json::from_str(&reply_line).context("解析 TUN 服务响应失败")?;
    if !reply.ok {
        bail!(
            reply
                .error
                .unwrap_or_else(|| "TUN 服务启动失败".to_string())
        );
    }
    let token = TunCancellationToken::new();
    let task_token = token.clone();
    let task = tokio::spawn(async move {
        tokio::select! {
            _ = task_token.cancelled() => {
                let _ = write_line(&mut w, &serde_json::json!({ "cmd": "stop" })).await;
                let _ = tokio::time::timeout(std::time::Duration::from_secs(5), lines.next_line()).await;
                Ok(())
            }
            line = lines.next_line() => {
                let msg = line
                    .ok()
                    .flatten()
                    .and_then(|l| serde_json::from_str::<TunReply>(&l).ok())
                    .and_then(|r| r.error)
                    .unwrap_or_else(|| "TUN 服务连接已断开".to_string());
                bail!(msg)
            }
        }
    });
    Ok((token, task))
}
