use anyhow::{Context, Result, bail, ensure};
use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::sync::{Arc, RwLock};
use tokio::io::{AsyncReadExt, AsyncWriteExt, copy_bidirectional};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinHandle;

pub struct HttpProxyBridge {
    local_addr: SocketAddr,
    configured_port: u16,
    allow_lan: bool,
    upstream_socks: Arc<RwLock<SocketAddr>>,
    accept_task: JoinHandle<()>,
}

impl HttpProxyBridge {
    pub async fn start(
        upstream_socks: SocketAddr,
        listen_port: u16,
        allow_lan: bool,
    ) -> Result<Self> {
        let bind_ip = if allow_lan {
            IpAddr::V4(Ipv4Addr::UNSPECIFIED)
        } else {
            IpAddr::V4(Ipv4Addr::LOCALHOST)
        };
        let listener = TcpListener::bind(SocketAddr::new(bind_ip, listen_port))
            .await
            .with_context(|| format!("bind local HTTP proxy on {bind_ip}:{listen_port}"))?;
        let local_addr = listener
            .local_addr()
            .context("read HTTP proxy local_addr")?;
        let upstream = Arc::new(RwLock::new(upstream_socks));
        let upstream_clone = upstream.clone();
        let accept_task = tokio::spawn(async move {
            while let Ok((client, _)) = listener.accept().await {
                let socks = *upstream_clone.read().expect("upstream lock poisoned");
                tokio::spawn(async move {
                    let _ = handle_http_client(client, socks).await;
                });
            }
        });
        Ok(Self {
            local_addr,
            configured_port: listen_port,
            allow_lan,
            upstream_socks: upstream,
            accept_task,
        })
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.local_addr
    }

    pub fn port(&self) -> u16 {
        self.local_addr.port()
    }

    pub fn matches_bind(&self, listen_port: u16, allow_lan: bool) -> bool {
        self.configured_port == listen_port && self.allow_lan == allow_lan
    }

    pub fn set_upstream(&self, upstream_socks: SocketAddr) {
        *self.upstream_socks.write().expect("upstream lock poisoned") = upstream_socks;
    }

    pub fn stop(self) {
        self.accept_task.abort();
    }
}

async fn handle_http_client(mut client: TcpStream, socks_addr: SocketAddr) -> Result<()> {
    let _ = client.set_nodelay(true);
    let mut buf = Vec::with_capacity(2048);
    let mut temp = [0u8; 2048];
    let first_n = client.read(&mut temp).await?;
    if first_n == 0 {
        return Ok(());
    }
    if temp[0] == 0x05 {
        let mut remote = TcpStream::connect(socks_addr)
            .await
            .with_context(|| format!("connect local SOCKS5 {socks_addr}"))?;
        let _ = remote.set_nodelay(true);
        remote.write_all(&temp[..first_n]).await?;
        let _ = copy_bidirectional(&mut client, &mut remote).await;
        return Ok(());
    }
    buf.extend_from_slice(&temp[..first_n]);
    let header_end = loop {
        if let Some(pos) = buf.windows(4).position(|w| w == b"\r\n\r\n") {
            break pos + 4;
        }
        ensure!(buf.len() <= 32 * 1024, "HTTP proxy header exceeds 32 KiB");
        let n = client.read(&mut temp).await?;
        if n == 0 {
            return Ok(());
        }
        buf.extend_from_slice(&temp[..n]);
    };

    let header_text =
        std::str::from_utf8(&buf[..header_end]).context("HTTP header is not UTF-8")?;
    let mut lines = header_text.split("\r\n");
    let request_line = lines.next().context("missing HTTP request line")?;
    let mut parts = request_line.split_whitespace();
    let method = parts.next().context("missing HTTP method")?;
    let target = parts.next().context("missing HTTP target")?;
    let version = parts.next().unwrap_or("HTTP/1.1");

    if method.eq_ignore_ascii_case("CONNECT") {
        let (host, port) = parse_authority(target, 443)?;
        match connect_via_socks5(socks_addr, &host, port).await {
            Ok(mut remote) => {
                client
                    .write_all(b"HTTP/1.1 200 Connection Established\r\n\r\n")
                    .await?;
                if buf.len() > header_end {
                    remote.write_all(&buf[header_end..]).await?;
                }
                let _ = copy_bidirectional(&mut client, &mut remote).await;
                Ok(())
            }
            Err(error) => {
                let body = format!("{error:#}");
                let resp = format!(
                    "HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                    body.len(),
                    body
                );
                let _ = client.write_all(resp.as_bytes()).await;
                Err(error)
            }
        }
    } else {
        let mut host_header = None;
        let mut kept_headers = Vec::new();
        for line in lines {
            if line.is_empty() {
                continue;
            }
            if let Some((name, val)) = line.split_once(':') {
                let trimmed_name = name.trim();
                if trimmed_name.eq_ignore_ascii_case("Proxy-Connection")
                    || trimmed_name.eq_ignore_ascii_case("Proxy-Authorization")
                {
                    continue;
                }
                if trimmed_name.eq_ignore_ascii_case("Host") {
                    host_header = Some(val.trim().to_string());
                }
                kept_headers.push(line);
            }
        }
        let (host, port, origin_path) = resolve_http_target(target, host_header.as_deref())?;
        match connect_via_socks5(socks_addr, &host, port).await {
            Ok(mut remote) => {
                let mut rewritten = format!("{method} {origin_path} {version}\r\n");
                for h in kept_headers {
                    rewritten.push_str(h);
                    rewritten.push_str("\r\n");
                }
                rewritten.push_str("\r\n");
                remote.write_all(rewritten.as_bytes()).await?;
                if buf.len() > header_end {
                    remote.write_all(&buf[header_end..]).await?;
                }
                let _ = copy_bidirectional(&mut client, &mut remote).await;
                Ok(())
            }
            Err(error) => {
                let body = format!("{error:#}");
                let resp = format!(
                    "HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                    body.len(),
                    body
                );
                let _ = client.write_all(resp.as_bytes()).await;
                Err(error)
            }
        }
    }
}

fn resolve_http_target(target: &str, host_header: Option<&str>) -> Result<(String, u16, String)> {
    if let Some(rest) = target
        .strip_prefix("http://")
        .or_else(|| target.strip_prefix("HTTP://"))
        .or_else(|| target.strip_prefix("ws://"))
        .or_else(|| target.strip_prefix("WS://"))
    {
        let (authority, path) = match rest.find('/') {
            Some(idx) => (&rest[..idx], &rest[idx..]),
            None => (rest, "/"),
        };
        let (host, port) = parse_authority(authority, 80)?;
        return Ok((host, port, path.to_string()));
    }
    let authority = host_header.context("HTTP request missing Host header")?;
    let (host, port) = parse_authority(authority, 80)?;
    let path = if target.is_empty() { "/" } else { target };
    Ok((host, port, path.to_string()))
}

fn parse_authority(authority: &str, default_port: u16) -> Result<(String, u16)> {
    let trimmed = authority.trim();
    if let Some(rest) = trimmed.strip_prefix('[') {
        let (v6, tail) = rest
            .split_once(']')
            .with_context(|| format!("invalid bracketed IPv6 authority: {trimmed}"))?;
        let port = if let Some(port_str) = tail.strip_prefix(':') {
            port_str.parse::<u16>()?
        } else {
            default_port
        };
        return Ok((v6.to_string(), port));
    }
    if let Some((host, port_str)) = trimmed.rsplit_once(':')
        && !host.contains(':')
    {
        return Ok((host.to_string(), port_str.parse::<u16>()?));
    }
    Ok((trimmed.to_string(), default_port))
}

pub async fn connect_via_socks5(
    socks_addr: SocketAddr,
    target_host: &str,
    target_port: u16,
) -> Result<TcpStream> {
    ensure!(
        target_host.len() <= u8::MAX as usize,
        "SOCKS5 target host is too long"
    );
    let mut stream = TcpStream::connect(socks_addr)
        .await
        .with_context(|| format!("connect local SOCKS5 {socks_addr}"))?;
    stream.write_all(&[0x05, 0x01, 0x00]).await?;
    let mut greeting = [0u8; 2];
    stream.read_exact(&mut greeting).await?;
    ensure!(
        greeting == [0x05, 0x00],
        "SOCKS5 greeting rejected: {greeting:02x?}"
    );
    let mut req = Vec::with_capacity(7 + target_host.len());
    req.extend_from_slice(&[0x05, 0x01, 0x00, 0x03, target_host.len() as u8]);
    req.extend_from_slice(target_host.as_bytes());
    req.extend_from_slice(&target_port.to_be_bytes());
    stream.write_all(&req).await?;
    let mut hdr = [0u8; 4];
    stream.read_exact(&mut hdr).await?;
    if hdr[0] != 0x05 || hdr[1] != 0x00 {
        bail!("SOCKS5 connect failed (reply 0x{:02x})", hdr[1]);
    }
    match hdr[3] {
        0x01 => {
            let mut skip = [0u8; 6];
            stream.read_exact(&mut skip).await?;
        }
        0x03 => {
            let mut len = [0u8; 1];
            stream.read_exact(&mut len).await?;
            let mut skip = vec![0u8; len[0] as usize + 2];
            stream.read_exact(&mut skip).await?;
        }
        0x04 => {
            let mut skip = [0u8; 18];
            stream.read_exact(&mut skip).await?;
        }
        other => bail!("unsupported SOCKS5 address type: {other}"),
    }
    Ok(stream)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_authorities_and_http_targets() -> Result<()> {
        assert_eq!(
            parse_authority("example.com:8443", 443)?,
            ("example.com".to_string(), 8443)
        );
        assert_eq!(
            parse_authority("[2001:db8::1]:443", 80)?,
            ("2001:db8::1".to_string(), 443)
        );
        assert_eq!(
            resolve_http_target("http://api.example.com:8080/v1/ping?x=1", None)?,
            (
                "api.example.com".to_string(),
                8080,
                "/v1/ping?x=1".to_string()
            )
        );
        Ok(())
    }
}
