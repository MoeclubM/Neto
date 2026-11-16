use super::*;

#[test]
fn udp_headers_accept_short_domains_and_reject_invalid_frames() {
    let packet = [0, 0, 0, 3, 1, b'a', 0, 53];
    let (target, header_len) = parse_socks5_udp_header(&packet).unwrap();
    assert_eq!(target, ProxyTarget::Domain("a".to_string(), 53));
    assert_eq!(header_len, packet.len());
    for length in 0..packet.len() {
        assert!(parse_socks5_udp_header(&packet[..length]).is_none());
    }
    let mut invalid = packet;
    invalid[0] = 1;
    assert!(parse_socks5_udp_header(&invalid).is_none());
    invalid[0] = 0;
    invalid[2] = 1;
    assert!(parse_socks5_udp_header(&invalid).is_none());
}

#[test]
fn discarded_runtime_entries_do_not_repopulate_closed_history() {
    let (guard, _) = register_connection(
        "discarded-runtime.invalid:443".to_string(),
        "tcp",
        String::new(),
        String::new(),
    );
    let id = guard.id;
    REGISTRY.lock().unwrap().remove(&id);
    drop(guard);
    assert!(
        !snapshot_connections()
            .closed
            .iter()
            .any(|entry| entry.id == id)
    );
}

#[tokio::test]
async fn close_connection_cancels_an_unfinished_upstream_handshake() -> Result<()> {
    let upstream = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).await?;
    let (ingress, task) = spawn_tracked_socks_ingress(
        upstream.local_addr()?,
        static_route_matcher("TEST", "DIRECT"),
    )
    .await?;
    let mut client = TcpStream::connect(ingress).await?;
    client.write_all(&[5, 1, 0]).await?;
    let mut greeting = [0; 2];
    client.read_exact(&mut greeting).await?;
    assert_eq!(greeting, [5, 0]);
    client
        .write_all(&[5, 1, 0, 1, 192, 0, 2, 99, 1, 187])
        .await?;
    let (mut remote, _) = upstream.accept().await?;
    let mut request = [0; 13];
    remote.read_exact(&mut request).await?;
    let id = snapshot_connections()
        .active
        .iter()
        .find(|entry| entry.host == "192.0.2.99:443")
        .expect("handshake should appear as an active connection")
        .id;
    assert!(close_connection(id));
    let mut probe = [0];
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(2), client.read(&mut probe)).await??,
        0
    );
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(2), remote.read(&mut probe)).await??,
        0
    );
    let snap = snapshot_connections();
    assert!(!snap.active.iter().any(|entry| entry.id == id));
    assert_eq!(snap.closed.iter().filter(|entry| entry.id == id).count(), 1);
    task.abort();
    Ok(())
}
