use std::{
    io::{self, Read, Write},
    net::TcpStream,
    time::{Duration, Instant},
};

fn exchange(stream: &mut TcpStream, bytes: &[u8], sent: impl FnOnce()) -> io::Result<Vec<u8>> {
    stream.set_nodelay(true)?;
    let deadline = Instant::now() + Duration::from_secs(15);
    let remaining = || {
        deadline
            .checked_duration_since(Instant::now())
            .filter(|v| !v.is_zero())
            .map(Some)
            .ok_or(io::ErrorKind::TimedOut)
    };
    for part in bytes.chunks(536) {
        let mut pending = part;
        while !pending.is_empty() {
            stream.set_write_timeout(remaining()?)?;
            match stream.write(pending) {
                Ok(0) => return Err(io::ErrorKind::WriteZero.into()),
                Ok(n) => pending = &pending[n..],
                Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
                Err(e) => return Err(e),
            }
        }
    }
    sent();
    let mut reply = Vec::new();
    let mut scratch = [0; 16384];
    let invalid = || io::Error::from(io::ErrorKind::InvalidData);
    let mut expected = None;
    let mut header_done = false;
    loop {
        stream.set_read_timeout(remaining()?)?;
        let n = match stream.read(&mut scratch) {
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            result => result?,
        };
        if n == 0 {
            return if header_done && expected.is_none() {
                Ok(reply)
            } else {
                Err(invalid())
            };
        }
        if reply.len() + n > 1024 * 1024 {
            return Err(invalid());
        }
        if reply.try_reserve(n).is_err() {
            return Err(io::Error::from(io::ErrorKind::OutOfMemory));
        }
        let previous = reply.len();
        reply.extend_from_slice(&scratch[..n]);
        if !header_done && reply.len() >= 4 {
            if reply[0] & 0x80 != 0 {
                let size = u32::from_be_bytes(reply[..4].try_into().unwrap()) & 0x7fff_ffff;
                expected = Some(24 + size as usize);
                header_done = true;
            } else if let Some(pos) = reply[previous.saturating_sub(3)..]
                .windows(4)
                .position(|v| v == b"\r\n\r\n")
            {
                let end = previous.saturating_sub(3) + pos + 4;
                if end > 16384 {
                    return Err(invalid());
                }
                for line in reply[..end].split(|&b| b == b'\n') {
                    if let Some(i) = line.iter().position(|&b| b == b':') {
                        if line[..i].eq_ignore_ascii_case(b"Content-Length") {
                            let size: usize = std::str::from_utf8(&line[i + 1..])
                                .ok()
                                .and_then(|s| s.trim().parse().ok())
                                .ok_or_else(invalid)?;
                            if expected.is_some() {
                                return Err(invalid());
                            }
                            expected = Some(end.checked_add(size).ok_or_else(invalid)?);
                        }
                    }
                }
                header_done = true;
            } else if reply.len() >= 16384 {
                return Err(invalid());
            }
        }
        if let Some(size) = expected {
            if size > 1024 * 1024 || reply.len() > size {
                return Err(invalid());
            }
            if reply.len() == size {
                return Ok(reply);
            }
        }
    }
}

#[cfg(target_os = "android")]
mod android {
    use super::*;
    use std::{ffi::c_void, os::fd::FromRawFd, slice, thread};
    #[repr(C)]
    pub struct Backend {
        open: extern "C" fn(bool) -> i32,
        interrupted: extern "C" fn() -> bool,
        serialize: extern "C" fn(*mut c_void, &mut i32) -> *mut u8,
        release: extern "C" fn(*mut c_void),
        preconnect: extern "C" fn(*mut c_void),
        finish: extern "C" fn(*mut c_void, i32, *mut u8, i32) -> bool,
    }
    #[no_mangle]
    pub unsafe extern "C" fn pl_rtsp_tcp(
        ctx: *mut c_void,
        pending: bool,
        ops: &Backend,
        error: &mut i32,
    ) -> bool {
        *error = -1;
        let mut fd = if pending { (ops.open)(true) } else { -111 };
        if fd < 0 && !matches!(fd, -111 | -104 | -32) {
            *error = -fd;
            return false;
        }
        for attempt in 0..=20 {
            if fd >= 0 {
                break;
            }
            if (ops.interrupted)() {
                return false;
            }
            fd = (ops.open)(false);
            if fd >= 0 {
                break;
            }
            *error = -fd;
            if fd != -111 || attempt == 20 {
                return false;
            }
            thread::sleep(Duration::from_millis(500));
        }
        let mut stream = TcpStream::from_raw_fd(fd);
        let mut size = 0;
        let raw = (ops.serialize)(ctx, &mut size);
        if raw.is_null() {
            return false;
        }
        let reply = if size > 0 {
            exchange(
                &mut stream,
                slice::from_raw_parts(raw, size as usize),
                || (ops.preconnect)(ctx),
            )
        } else {
            Err(io::Error::from(io::ErrorKind::InvalidData))
        };
        (ops.release)(raw.cast());
        match reply {
            Ok(mut bytes) => (ops.finish)(ctx, fd, bytes.as_mut_ptr(), bytes.len() as i32),
            Err(e) => {
                *error = match e.kind() {
                    io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut => 110,
                    _ => e.raw_os_error().unwrap_or(-1),
                };
                false
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{net::TcpListener, thread};
    #[test]
    fn full_requests_and_split_responses_survive_socket_boundaries() {
        let listen = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listen.local_addr().unwrap();
        let req = vec![0xb6; 19661];
        let mut reply = b"RTSP/1.0 200 OK\r\nContent-Length: 57001\r\n\r\n".to_vec();
        reply.extend(vec![0x78; 57001]);
        let request = req.clone();
        let expected = reply.clone();
        let server = thread::spawn(move || {
            let (mut sock, _) = listen.accept().unwrap();
            let mut body = vec![0; request.len()];
            sock.read_exact(&mut body).unwrap();
            assert_eq!(body, request);
            for chunk in expected.chunks(241) {
                sock.write_all(chunk).unwrap();
            }
        });
        let mut notified = false;
        let resp = exchange(&mut TcpStream::connect(addr).unwrap(), &req, || {
            notified = true
        })
        .unwrap();
        assert!(notified);
        assert_eq!(resp, reply);
        server.join().unwrap();
    }
}
