use crate::telemetry::now_ms;
use aerion::{RouteNetwork, protocol::ProxyTarget};
use anyhow::{Context, Result, bail, ensure};
use once_cell::sync::Lazy;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, VecDeque};
use std::io;
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::task::{Context as TaskContext, Poll};
use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt, ReadBuf, copy_bidirectional};
use tokio::net::{TcpListener, TcpStream, UdpSocket};
use tokio::sync::watch;
use tokio::task::JoinHandle;

pub type RouteMatchFn =
    Arc<dyn Fn(&ProxyTarget, RouteNetwork) -> (String, String) + Send + Sync + 'static>;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActiveConnectionEntry {
    pub id: u64,
    pub host: String,
    pub network: String,
    pub rule: String,
    pub outbound: String,
    pub upload_bytes: u64,
    pub download_bytes: u64,
    #[serde(default)]
    pub upload_speed_bps: u64,
    #[serde(default)]
    pub download_speed_bps: u64,
    pub started_at_ms: u64,
    #[serde(default)]
    pub closed_at_ms: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ConnectionsSnapshot {
    pub active: Vec<ActiveConnectionEntry>,
    #[serde(default)]
    pub closed: Vec<ActiveConnectionEntry>,
    pub total_upload_bytes: u64,
    pub total_download_bytes: u64,
    #[serde(default)]
    pub total_upload_speed_bps: u64,
    #[serde(default)]
    pub total_download_speed_bps: u64,
}

struct TrackedEntry {
    id: u64,
    network: &'static str,
    meta: Mutex<(String, String, String)>,
    upload_bytes: AtomicU64,
    download_bytes: AtomicU64,
    rate: Mutex<[u64; 5]>,
    started_at_ms: u64,
    cancel_tx: watch::Sender<bool>,
}

const CLOSED_LIMIT: usize = 300;

static NEXT_CONN_ID: AtomicU64 = AtomicU64::new(1);
static SESSION_UPLOAD_BYTES: AtomicU64 = AtomicU64::new(0);
static SESSION_DOWNLOAD_BYTES: AtomicU64 = AtomicU64::new(0);
static REGISTRY: Lazy<Mutex<BTreeMap<u64, Arc<TrackedEntry>>>> =
    Lazy::new(|| Mutex::new(BTreeMap::new()));
static CLOSED: Lazy<Mutex<VecDeque<ActiveConnectionEntry>>> =
    Lazy::new(|| Mutex::new(VecDeque::with_capacity(CLOSED_LIMIT)));

impl TrackedEntry {
    fn view(&self, closed_at_ms: u64) -> ActiveConnectionEntry {
        let (host, rule, outbound) = self.meta.lock().map(|g| g.clone()).unwrap_or_default();
        let upload_bytes = self.upload_bytes.load(Ordering::Relaxed);
        let download_bytes = self.download_bytes.load(Ordering::Relaxed);
        let (upload_speed_bps, download_speed_bps) = if closed_at_ms == 0 {
            self.sample_rate(upload_bytes, download_bytes)
        } else {
            (0, 0)
        };
        ActiveConnectionEntry {
            id: self.id,
            host,
            network: self.network.to_string(),
            rule,
            outbound,
            upload_bytes,
            download_bytes,
            upload_speed_bps,
            download_speed_bps,
            started_at_ms: self.started_at_ms,
            closed_at_ms,
        }
    }

    fn sample_rate(&self, up: u64, down: u64) -> (u64, u64) {
        let Ok(mut rate) = self.rate.lock() else {
            return (0, 0);
        };
        let now = now_ms();
        let elapsed = now.saturating_sub(rate[0]);
        if elapsed >= 500 {
            rate[3] = up.saturating_sub(rate[1]) * 1000 / elapsed;
            rate[4] = down.saturating_sub(rate[2]) * 1000 / elapsed;
            *rate = [now, up, down, rate[3], rate[4]];
        }
        (rate[3], rate[4])
    }
}

struct ConnectionGuard {
    id: u64,
    entry: Arc<TrackedEntry>,
}

impl Drop for ConnectionGuard {
    fn drop(&mut self) {
        if let Ok(mut map) = REGISTRY.lock() {
            map.remove(&self.id);
        }
        if let Ok(mut closed) = CLOSED.lock() {
            if closed.len() >= CLOSED_LIMIT {
                closed.pop_front();
            }
            closed.push_back(self.entry.view(now_ms()));
        }
    }
}

fn register_connection(
    host: String,
    network: &'static str,
    rule: String,
    outbound: String,
) -> (ConnectionGuard, watch::Receiver<bool>) {
    let id = NEXT_CONN_ID.fetch_add(1, Ordering::Relaxed);
    let (cancel_tx, cancel_rx) = watch::channel(false);
    let started_at_ms = now_ms();
    let entry = Arc::new(TrackedEntry {
        id,
        network,
        meta: Mutex::new((host, rule, outbound)),
        upload_bytes: AtomicU64::new(0),
        download_bytes: AtomicU64::new(0),
        rate: Mutex::new([started_at_ms, 0, 0, 0, 0]),
        started_at_ms,
        cancel_tx,
    });
    REGISTRY
        .lock()
        .expect("connection registry lock poisoned")
        .insert(id, entry.clone());
    (ConnectionGuard { id, entry }, cancel_rx)
}

pub fn clear_connections() {
    let entries: Vec<Arc<TrackedEntry>> = {
        let mut map = REGISTRY.lock().expect("connection registry lock poisoned");
        let vals = map.values().cloned().collect();
        map.clear();
        vals
    };
    for entry in entries {
        let _ = entry.cancel_tx.send(true);
    }
    clear_closed_connections();
    SESSION_UPLOAD_BYTES.store(0, Ordering::Relaxed);
    SESSION_DOWNLOAD_BYTES.store(0, Ordering::Relaxed);
}

pub fn clear_closed_connections() {
    if let Ok(mut closed) = CLOSED.lock() {
        closed.clear();
    }
}

pub fn close_connection(id: u64) -> bool {
    let found = REGISTRY
        .lock()
        .expect("connection registry lock poisoned")
        .remove(&id);
    if let Some(entry) = found {
        let _ = entry.cancel_tx.send(true);
        true
    } else {
        false
    }
}

pub fn close_all_connections() -> usize {
    let entries: Vec<Arc<TrackedEntry>> = {
        let mut map = REGISTRY.lock().expect("connection registry lock poisoned");
        let vals = map.values().cloned().collect();
        map.clear();
        vals
    };
    let count = entries.len();
    for entry in entries {
        let _ = entry.cancel_tx.send(true);
    }
    count
}

pub fn active_connection_count() -> usize {
    REGISTRY.lock().map(|m| m.len()).unwrap_or(0)
}

pub fn session_traffic_totals() -> (u64, u64) {
    (
        SESSION_UPLOAD_BYTES.load(Ordering::Relaxed),
        SESSION_DOWNLOAD_BYTES.load(Ordering::Relaxed),
    )
}

pub fn snapshot_connections() -> ConnectionsSnapshot {
    let entries: Vec<Arc<TrackedEntry>> = REGISTRY
        .lock()
        .expect("connection registry lock poisoned")
        .values()
        .cloned()
        .collect();
    let mut active: Vec<_> = entries.iter().map(|item| item.view(0)).collect();
    active.sort_by(|a, b| b.id.cmp(&a.id));
    let total_upload_speed_bps = active.iter().map(|item| item.upload_speed_bps).sum();
    let total_download_speed_bps = active.iter().map(|item| item.download_speed_bps).sum();
    let closed = CLOSED
        .lock()
        .map(|c| c.iter().rev().cloned().collect())
        .unwrap_or_default();
    let (total_upload_bytes, total_download_bytes) = session_traffic_totals();
    ConnectionsSnapshot {
        active,
        closed,
        total_upload_bytes,
        total_download_bytes,
        total_upload_speed_bps,
        total_download_speed_bps,
    }
}

pub fn static_route_matcher(rule: impl Into<String>, outbound: impl Into<String>) -> RouteMatchFn {
    let r = rule.into();
    let o = outbound.into();
    Arc::new(move |_, _| (r.clone(), o.clone()))
}

pub async fn spawn_tracked_socks_ingress(
    inner_socks: SocketAddr,
    matcher: RouteMatchFn,
) -> Result<(SocketAddr, JoinHandle<()>)> {
    let listener = TcpListener::bind(SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), 0))
        .await
        .context("bind tracked SOCKS5 ingress")?;
    let local_addr = listener.local_addr().context("tracked SOCKS5 local_addr")?;
    let task = tokio::spawn(async move {
        while let Ok((client, _)) = listener.accept().await {
            let _ = client.set_nodelay(true);
            let matcher = matcher.clone();
            tokio::spawn(async move {
                let _ = handle_tracked_socks_client(client, inner_socks, matcher).await;
            });
        }
    });
    Ok((local_addr, task))
}

struct CountedStream<'a> {
    inner: &'a mut TcpStream,
    entry: &'a TrackedEntry,
}

impl AsyncRead for CountedStream<'_> {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut TaskContext<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let before = buf.filled().len();
        let poll = Pin::new(&mut *self.inner).poll_read(cx, buf);
        if let Poll::Ready(Ok(())) = &poll {
            let delta = buf.filled().len().saturating_sub(before) as u64;
            if delta > 0 {
                self.entry.upload_bytes.fetch_add(delta, Ordering::Relaxed);
                SESSION_UPLOAD_BYTES.fetch_add(delta, Ordering::Relaxed);
            }
        }
        poll
    }
}

impl AsyncWrite for CountedStream<'_> {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut TaskContext<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        let poll = Pin::new(&mut *self.inner).poll_write(cx, buf);
        if let Poll::Ready(Ok(n)) = &poll
            && *n > 0
        {
            let delta = *n as u64;
            self.entry.download_bytes.fetch_add(delta, Ordering::Relaxed);
            SESSION_DOWNLOAD_BYTES.fetch_add(delta, Ordering::Relaxed);
        }
        poll
    }

    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut TaskContext<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_flush(cx)
    }

    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut TaskContext<'_>) -> Poll<io::Result<()>> {
        Pin::new(&mut *self.inner).poll_shutdown(cx)
    }
}

fn format_proxy_target(target: &ProxyTarget) -> String {
    match target {
        ProxyTarget::Ip(addr) => addr.to_string(),
        ProxyTarget::Domain(host, port) => format!("{host}:{port}"),
    }
}

async fn read_socks5_request(stream: &mut TcpStream) -> Result<(u8, ProxyTarget, Vec<u8>)> {
    let mut greet_hdr = [0u8; 2];
    stream.read_exact(&mut greet_hdr).await?;
    ensure!(greet_hdr[0] == 0x05, "invalid SOCKS5 version {}", greet_hdr[0]);
    let mut methods = vec![0u8; greet_hdr[1] as usize];
    stream.read_exact(&mut methods).await?;
    stream.write_all(&[0x05, 0x00]).await?;

    let mut hdr = [0u8; 4];
    stream.read_exact(&mut hdr).await?;
    ensure!(hdr[0] == 0x05, "invalid SOCKS5 request version {}", hdr[0]);
    let cmd = hdr[1];
    let atyp = hdr[3];
    let mut raw = Vec::with_capacity(32);
    raw.extend_from_slice(&hdr);
    let target = match atyp {
        0x01 => {
            let mut addr = [0u8; 6];
            stream.read_exact(&mut addr).await?;
            raw.extend_from_slice(&addr);
            let ip = Ipv4Addr::new(addr[0], addr[1], addr[2], addr[3]);
            let port = u16::from_be_bytes([addr[4], addr[5]]);
            ProxyTarget::Ip(SocketAddr::new(IpAddr::V4(ip), port))
        }
        0x03 => {
            let mut len = [0u8; 1];
            stream.read_exact(&mut len).await?;
            raw.push(len[0]);
            let mut buf = vec![0u8; len[0] as usize + 2];
            stream.read_exact(&mut buf).await?;
            raw.extend_from_slice(&buf);
            let dlen = len[0] as usize;
            let domain = String::from_utf8_lossy(&buf[..dlen]).into_owned();
            let port = u16::from_be_bytes([buf[dlen], buf[dlen + 1]]);
            if let Ok(ip) = domain.parse::<IpAddr>() {
                ProxyTarget::Ip(SocketAddr::new(ip, port))
            } else {
                ProxyTarget::Domain(domain, port)
            }
        }
        0x04 => {
            let mut addr = [0u8; 18];
            stream.read_exact(&mut addr).await?;
            raw.extend_from_slice(&addr);
            let mut octets = [0u8; 16];
            octets.copy_from_slice(&addr[..16]);
            let ip = Ipv6Addr::from(octets);
            let port = u16::from_be_bytes([addr[16], addr[17]]);
            ProxyTarget::Ip(SocketAddr::new(IpAddr::V6(ip), port))
        }
        other => bail!("unsupported SOCKS5 ATYP {other}"),
    };
    Ok((cmd, target, raw))
}

async fn read_socks5_reply(stream: &mut TcpStream) -> Result<(u8, SocketAddr, Vec<u8>)> {
    let mut hdr = [0u8; 4];
    stream.read_exact(&mut hdr).await?;
    let rep = hdr[1];
    let atyp = hdr[3];
    let mut raw = Vec::with_capacity(24);
    raw.extend_from_slice(&hdr);
    let bound = match atyp {
        0x01 => {
            let mut b = [0u8; 6];
            stream.read_exact(&mut b).await?;
            raw.extend_from_slice(&b);
            SocketAddr::new(
                IpAddr::V4(Ipv4Addr::new(b[0], b[1], b[2], b[3])),
                u16::from_be_bytes([b[4], b[5]]),
            )
        }
        0x03 => {
            let mut len = [0u8; 1];
            stream.read_exact(&mut len).await?;
            raw.push(len[0]);
            let mut b = vec![0u8; len[0] as usize + 2];
            stream.read_exact(&mut b).await?;
            raw.extend_from_slice(&b);
            let port = u16::from_be_bytes([b[len[0] as usize], b[len[0] as usize + 1]]);
            SocketAddr::new(IpAddr::V4(Ipv4Addr::LOCALHOST), port)
        }
        0x04 => {
            let mut b = [0u8; 18];
            stream.read_exact(&mut b).await?;
            raw.extend_from_slice(&b);
            let mut octets = [0u8; 16];
            octets.copy_from_slice(&b[..16]);
            SocketAddr::new(
                IpAddr::V6(Ipv6Addr::from(octets)),
                u16::from_be_bytes([b[16], b[17]]),
            )
        }
        other => bail!("unsupported SOCKS5 reply ATYP {other}"),
    };
    Ok((rep, bound, raw))
}

fn parse_socks5_udp_header(pkt: &[u8]) -> Option<(ProxyTarget, usize)> {
    if pkt.len() < 10 || pkt[2] != 0 {
        return None;
    }
    match pkt[3] {
        0x01 => {
            let ip = Ipv4Addr::new(pkt[4], pkt[5], pkt[6], pkt[7]);
            let port = u16::from_be_bytes([pkt[8], pkt[9]]);
            Some((ProxyTarget::Ip(SocketAddr::new(IpAddr::V4(ip), port)), 10))
        }
        0x03 => {
            let dlen = pkt[4] as usize;
            if pkt.len() < 7 + dlen {
                return None;
            }
            let domain = String::from_utf8_lossy(&pkt[5..5 + dlen]).into_owned();
            let port = u16::from_be_bytes([pkt[5 + dlen], pkt[6 + dlen]]);
            let target = if let Ok(ip) = domain.parse::<IpAddr>() {
                ProxyTarget::Ip(SocketAddr::new(ip, port))
            } else {
                ProxyTarget::Domain(domain, port)
            };
            Some((target, 7 + dlen))
        }
        0x04 => {
            if pkt.len() < 22 {
                return None;
            }
            let mut octets = [0u8; 16];
            octets.copy_from_slice(&pkt[4..20]);
            let port = u16::from_be_bytes([pkt[20], pkt[21]]);
            Some((
                ProxyTarget::Ip(SocketAddr::new(IpAddr::V6(Ipv6Addr::from(octets)), port)),
                22,
            ))
        }
        _ => None,
    }
}

async fn handle_tracked_socks_client(
    mut client: TcpStream,
    inner_socks: SocketAddr,
    matcher: RouteMatchFn,
) -> Result<()> {
    let (cmd, target, raw_req) = read_socks5_request(&mut client).await?;
    match cmd {
        0x01 => {
            let (rule, outbound) = matcher(&target, RouteNetwork::Tcp);
            let (guard, mut cancel_rx) =
                register_connection(format_proxy_target(&target), "tcp", rule, outbound);

            let mut upstream = TcpStream::connect(inner_socks).await?;
            let _ = upstream.set_nodelay(true);
            upstream.write_all(&[0x05, 0x01, 0x00]).await?;
            let mut up_greet = [0u8; 2];
            upstream.read_exact(&mut up_greet).await?;
            ensure!(up_greet == [0x05, 0x00], "inner SOCKS5 greeting rejected");
            upstream.write_all(&raw_req).await?;
            let (rep, _, raw_reply) = read_socks5_reply(&mut upstream).await?;
            client.write_all(&raw_reply).await?;
            if rep != 0x00 {
                return Ok(());
            }

            let mut counted_client = CountedStream {
                inner: &mut client,
                entry: &guard.entry,
            };
            tokio::select! {
                _ = copy_bidirectional(&mut counted_client, &mut upstream) => {}
                _ = cancel_rx.changed() => {}
            }
            Ok(())
        }
        0x03 => {
            let mut upstream_ctrl = TcpStream::connect(inner_socks).await?;
            let _ = upstream_ctrl.set_nodelay(true);
            upstream_ctrl.write_all(&[0x05, 0x01, 0x00]).await?;
            let mut up_greet = [0u8; 2];
            upstream_ctrl.read_exact(&mut up_greet).await?;
            ensure!(up_greet == [0x05, 0x00], "inner SOCKS5 UDP greeting rejected");
            upstream_ctrl
                .write_all(&[0x05, 0x03, 0x00, 0x01, 0, 0, 0, 0, 0, 0])
                .await?;
            let (rep, mut up_udp_addr, raw_reply) = read_socks5_reply(&mut upstream_ctrl).await?;
            if rep != 0x00 {
                let _ = client.write_all(&raw_reply).await;
                return Ok(());
            }
            if up_udp_addr.ip().is_unspecified() {
                up_udp_addr.set_ip(inner_socks.ip());
            }

            let client_udp = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).await?;
            let relay_udp = UdpSocket::bind((Ipv4Addr::LOCALHOST, 0)).await?;
            relay_udp.connect(up_udp_addr).await?;
            let local_port = client_udp.local_addr()?.port();
            let mut reply = [0x05u8, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0];
            reply[8..10].copy_from_slice(&local_port.to_be_bytes());
            client.write_all(&reply).await?;

            let initial_host = match &target {
                ProxyTarget::Ip(addr) if addr.ip().is_unspecified() && addr.port() == 0 => {
                    "UDP 会话".to_string()
                }
                other => format_proxy_target(other),
            };
            let (init_rule, init_out) = matcher(&target, RouteNetwork::Udp);
            let (guard, mut cancel_rx) =
                register_connection(initial_host, "udp", init_rule, init_out);

            let mut c_ctrl_buf = [0u8; 16];
            let mut u_ctrl_buf = [0u8; 16];
            let mut cbuf = vec![0u8; 65535];
            let mut rbuf = vec![0u8; 65535];
            let mut client_peer: Option<SocketAddr> = None;
            let mut target_updated = false;

            loop {
                tokio::select! {
                    _ = cancel_rx.changed() => break,
                    r = client.read(&mut c_ctrl_buf) => {
                        if matches!(r, Ok(0) | Err(_)) {
                            break;
                        }
                    }
                    r = upstream_ctrl.read(&mut u_ctrl_buf) => {
                        if matches!(r, Ok(0) | Err(_)) {
                            break;
                        }
                    }
                    Ok((n, peer)) = client_udp.recv_from(&mut cbuf) => {
                        client_peer = Some(peer);
                        if let Some((dst, hdr_len)) = parse_socks5_udp_header(&cbuf[..n]) {
                            if !target_updated {
                                let (rule, out) = matcher(&dst, RouteNetwork::Udp);
                                if let Ok(mut meta) = guard.entry.meta.lock() {
                                    *meta = (format_proxy_target(&dst), rule, out);
                                }
                                target_updated = true;
                            }
                            let payload = n.saturating_sub(hdr_len) as u64;
                            guard.entry.upload_bytes.fetch_add(payload, Ordering::Relaxed);
                            SESSION_UPLOAD_BYTES.fetch_add(payload, Ordering::Relaxed);
                        }
                        let _ = relay_udp.send(&cbuf[..n]).await;
                    }
                    Ok(n) = relay_udp.recv(&mut rbuf) => {
                        if let Some(peer) = client_peer {
                            let hdr_len = parse_socks5_udp_header(&rbuf[..n])
                                .map(|(_, l)| l)
                                .unwrap_or(10);
                            let payload = n.saturating_sub(hdr_len) as u64;
                            guard.entry.download_bytes.fetch_add(payload, Ordering::Relaxed);
                            SESSION_DOWNLOAD_BYTES.fetch_add(payload, Ordering::Relaxed);
                            let _ = client_udp.send_to(&rbuf[..n], peer).await;
                        }
                    }
                }
            }
            Ok(())
        }
        other => {
            let _ = client
                .write_all(&[0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0])
                .await;
            bail!("unsupported SOCKS5 command 0x{other:02x}");
        }
    }
}
