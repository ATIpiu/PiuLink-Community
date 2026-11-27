//! Single-client LAN bridge for the default Sunshine GameStream port layout.
//! The existing Moonlight and Sunshine endpoints retain their pairing and encryption.
use std::io::{self, ErrorKind, Read, Write};
use std::net::{IpAddr, Shutdown, SocketAddr, TcpListener, TcpStream, UdpSocket};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

fn relay(
    mut source: TcpStream,
    mut destination: TcpStream,
    activity: Arc<Mutex<Instant>>,
) -> io::Result<()> {
    let mut buffer = [0; 16384];
    loop {
        match source.read(&mut buffer) {
            Ok(0) => return destination.shutdown(Shutdown::Write),
            Ok(n) => {
                destination.write_all(&buffer[..n])?;
                *activity.lock().unwrap() = Instant::now();
            }
            Err(e) if matches!(e.kind(), ErrorKind::TimedOut | ErrorKind::WouldBlock) => {
                if activity.lock().unwrap().elapsed() >= Duration::from_secs(130) {
                    return Err(io::ErrorKind::TimedOut.into());
                }
            }
            Err(e) if e.kind() == ErrorKind::Interrupted => continue,
            Err(e) => return Err(e),
        }
    }
}

fn tcp_connection(client: TcpStream, upstream: SocketAddr) -> io::Result<()> {
    let host = TcpStream::connect_timeout(&upstream, Duration::from_secs(4))?;
    for sock in [&host, &client] {
        sock.set_nodelay(true)?;
        sock.set_read_timeout(Some(Duration::from_secs(1)))?;
        sock.set_write_timeout(Some(Duration::from_secs(10)))?;
    }
    let activity = Arc::new(Mutex::new(Instant::now()));
    let upstream_activity = activity.clone();
    let from_client = client.try_clone()?;
    let to_host = host.try_clone()?;
    let send =
        thread::Builder::new().spawn(move || relay(from_client, to_host, upstream_activity))?;
    let result = relay(host.try_clone()?, client.try_clone()?, activity);
    if result.is_err() {
        let _ = client.shutdown(Shutdown::Both);
        let _ = host.shutdown(Shutdown::Both);
    }
    send.join()
        .map_err(|_| io::Error::other("TCP worker stopped"))??;
    result
}

fn tcp(
    listener: TcpListener,
    host: SocketAddr,
    allowed: IpAddr,
    active: Arc<AtomicUsize>,
) -> io::Result<()> {
    for stream in listener.incoming() {
        let stream = stream?;
        if stream.peer_addr()?.ip() != allowed
            || active
                .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |n| {
                    (n < 16).then_some(n + 1)
                })
                .is_err()
        {
            continue;
        }
        let slot = active.clone();
        if thread::Builder::new()
            .spawn(move || {
                if let Err(e) = tcp_connection(stream, host) {
                    eprintln!("TCP {}: {e}", host.port());
                }
                slot.fetch_sub(1, Ordering::Relaxed);
            })
            .is_err()
        {
            active.fetch_sub(1, Ordering::Relaxed);
        }
    }
    Ok(())
}

fn transient(e: &io::Error) -> bool {
    matches!(
        e.kind(),
        ErrorKind::WouldBlock
            | ErrorKind::ConnectionRefused
            | ErrorKind::ConnectionReset
            | ErrorKind::Interrupted
    )
}

fn udp(front: &UdpSocket, host: SocketAddr, allowed: IpAddr) -> io::Result<()> {
    front.set_nonblocking(false)?;
    let mut packet = [0; 65535];
    loop {
        let (size, client) = match front.recv_from(&mut packet) {
            Ok(value) => value,
            Err(e) if transient(&e) => continue,
            Err(e) => return Err(e),
        };
        if client.ip() != allowed {
            continue;
        }
        let back = UdpSocket::bind((std::net::Ipv4Addr::UNSPECIFIED, 0))?;
        back.connect(host)?;
        match back.send(&packet[..size]) {
            Ok(_) => (),
            Err(e) if transient(&e) => continue,
            Err(e) => return Err(e),
        }
        front.set_nonblocking(true)?;
        back.set_nonblocking(true)?;
        let mut last = Instant::now();
        let mut down = 0u64;
        let mut up = 1u64;
        let mut last_report = Instant::now();
        while last.elapsed() < Duration::from_secs(12) {
            let mut busy = false;
            match front.recv_from(&mut packet) {
                Ok((n, peer)) if peer == client => {
                    match back.send(&packet[..n]) {
                        Ok(_) => (),
                        Err(e) if transient(&e) => continue,
                        Err(e) => return Err(e),
                    }
                    up += 1;
                    busy = true;
                }
                Ok(_) => (),
                Err(e) if transient(&e) => (),
                Err(e) => return Err(e),
            }
            match back.recv(&mut packet) {
                Ok(n) => {
                    match front.send_to(&packet[..n], client) {
                        Ok(_) => (),
                        Err(e) if transient(&e) => continue,
                        Err(e) => return Err(e),
                    }
                    down += 1;
                    busy = true;
                }
                Err(e)
                    if matches!(
                        e.kind(),
                        ErrorKind::WouldBlock
                            | ErrorKind::ConnectionRefused
                            | ErrorKind::ConnectionReset
                    ) =>
                {
                    ()
                }
                Err(e) => return Err(e),
            }
            if last_report.elapsed() >= Duration::from_secs(1) {
                eprintln!("udp_port={} to_host={} to_client={}", host.port(), up, down);
                last_report = Instant::now();
            }
            if busy {
                last = Instant::now();
            } else {
                thread::sleep(Duration::from_micros(250));
            }
        }
        front.set_nonblocking(false)?;
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if !(3..=4).contains(&args.len()) {
        return Err(
            "usage: gamestream-bridge BIND_IPV4 SUNSHINE_IPV4 ALLOWED_CLIENT_IPV4 [HTTP_PORT]"
                .into(),
        );
    }
    let ip = |i: usize| args[i].parse::<std::net::Ipv4Addr>().map(IpAddr::V4);
    let (bind, host, client) = (ip(0)?, ip(1)?, ip(2)?);
    if bind.is_unspecified() || host.is_unspecified() || client.is_unspecified() || bind == host {
        return Err("use specific interface addresses and a distinct Sunshine host".into());
    }
    // Reserve every port before announcing readiness. The admin web port is excluded.
    let base = args
        .get(3)
        .map(|p| p.parse::<u16>())
        .transpose()?
        .unwrap_or(47989);
    if !(1029..=65514).contains(&base) {
        return Err("base port out of range".into());
    }
    let mut listeners = Vec::new();
    for p in [base - 5, base, base + 21] {
        listeners.push((p, TcpListener::bind((bind, p))?));
    }
    let mut sockets = Vec::new();
    for p in [base + 9, base + 10, base + 11] {
        sockets.push((p, UdpSocket::bind((bind, p))?));
    }
    let (tx, rx) = std::sync::mpsc::channel();
    let connections = Arc::new(AtomicUsize::new(0));
    for (port, listener) in listeners {
        let active = connections.clone();
        let tx = tx.clone();
        thread::spawn(move || {
            let _ = tx.send(tcp(listener, (host, port).into(), client, active));
        });
    }
    for (port, socket) in sockets {
        thread::spawn(move || loop {
            if let Err(error) = udp(&socket, (host, port).into(), client) {
                eprintln!("UDP {port}: {error}");
                thread::sleep(Duration::from_millis(500));
            }
        });
    }
    if std::env::var_os("PIULINK_PARENT_PIPE").is_some() {
        thread::spawn(|| {
            let mut stop = String::new();
            let _ = std::io::stdin().read_line(&mut stop);
            std::process::exit(0);
        });
    }
    eprintln!("GameStream bridge ready");
    rx.recv()??;
    Ok(())
}
