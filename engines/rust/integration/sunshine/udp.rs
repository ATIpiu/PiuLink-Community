use std::{
    mem::{align_of, size_of},
    ptr, slice,
};
use windows_sys::Win32::Networking::WinSock::*;

#[repr(C)]
pub struct Span {
    data: *const u8,
    len: usize,
}
#[repr(C)]
pub struct Batch {
    socket: usize,
    target: *const SOCKADDR,
    target_len: i32,
    source: *const u8,
    source_v6: bool,
    headers: *const u8,
    header_size: usize,
    payloads: *const Span,
    spans: usize,
    unit: usize,
    offset: usize,
    count: usize,
}
struct Segments {
    buffers: [WSABUF; 128],
    used: usize,
}
impl Segments {
    fn push(&mut self, data: *const u8, len: usize) -> Option<()> {
        if len == 0 {
            return Some(());
        }
        if data.is_null() {
            return None;
        }
        *self.buffers.get_mut(self.used)? = WSABUF {
            buf: data.cast_mut(),
            len: u32::try_from(len).ok()?,
        };
        self.used += 1;
        Some(())
    }
    unsafe fn build(b: &Batch) -> Option<Self> {
        if b.unit == 0
            || !(1..=64).contains(&b.count)
            || b.spans == 0
            || b.payloads.is_null()
            || (b.header_size != 0 && b.headers.is_null())
        {
            return None;
        }
        let mut out = Self {
            buffers: [WSABUF::default(); 128],
            used: 0,
        };
        let spans = slice::from_raw_parts(b.payloads, b.spans);
        let mut span = 0;
        let mut off = b.offset.checked_mul(b.unit)?;
        while off >= spans.get(span)?.len {
            off -= spans[span].len;
            span += 1
        }
        let mut remaining = b.count.checked_mul(b.unit)?;
        for i in 0..b.count {
            let mut needed = if b.header_size != 0 {
                let header_offset = b.offset.checked_add(i)?.checked_mul(b.header_size)?;
                out.push(b.headers.add(header_offset), b.header_size)?;
                b.unit
            } else {
                remaining
            };
            while needed != 0 {
                let current = spans.get(span)?;
                if current.data.is_null() {
                    return None;
                }
                let bytes = (current.len - off).min(needed);
                out.push(current.data.add(off), bytes)?;
                off += bytes;
                needed -= bytes;
                remaining -= bytes;
                if off == current.len {
                    span += 1;
                    off = 0;
                }
            }
            if remaining == 0 {
                break;
            }
        }
        Some(out)
    }
}
#[repr(align(8))]
struct Control([u8; 80]);
impl Control {
    fn append(&mut self, offset: usize, level: i32, kind: i32, data: &[u8]) -> usize {
        let head = size_of::<CMSGHDR>();
        let length = head + data.len();
        let aligned = length.next_multiple_of(align_of::<CMSGHDR>());
        let segment = &mut self.0[offset..offset + aligned];
        let header = CMSGHDR {
            cmsg_len: length,
            cmsg_level: level,
            cmsg_type: kind,
        };
        unsafe {
            ptr::write(segment.as_mut_ptr().cast::<CMSGHDR>(), header);
        }
        segment[head..length].copy_from_slice(data);
        offset + aligned
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_udp_send(b: &Batch) -> bool {
    if b.target.is_null() || b.source.is_null() || !matches!(b.target_len, 16 | 28) {
        return false;
    }
    let Some(segment_size) = b
        .header_size
        .checked_add(b.unit)
        .and_then(|n| u32::try_from(n).ok())
    else {
        return false;
    };
    let Some(mut segments) = Segments::build(b) else {
        return false;
    };
    let mut control = Control([0; 80]);
    let addr_len = if b.source_v6 { 16 } else { 4 };
    let mut packet_info = [0u8; 20];
    packet_info[..addr_len].copy_from_slice(slice::from_raw_parts(b.source, addr_len));
    let (level, kind) = if b.source_v6 {
        (IPPROTO_IPV6, IPV6_PKTINFO)
    } else {
        (IPPROTO_IP, IP_PKTINFO)
    };
    let mut used = control.append(0, level, kind, &packet_info[..addr_len + 4]);
    if b.count > 1 {
        used = control.append(
            used,
            IPPROTO_UDP,
            UDP_SEND_MSG_SIZE,
            &segment_size.to_ne_bytes(),
        );
    }
    let msg = WSAMSG {
        name: b.target.cast_mut(),
        namelen: b.target_len,
        lpBuffers: segments.buffers.as_mut_ptr(),
        dwBufferCount: segments.used as u32,
        Control: WSABUF {
            buf: control.0.as_mut_ptr(),
            len: used as u32,
        },
        dwFlags: 0,
    };
    let mut bytes = 0;
    WSASendMsg(b.socket, &msg, 0, &mut bytes, ptr::null_mut(), None) != SOCKET_ERROR
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::{net::UdpSocket, os::windows::io::AsRawSocket, time::Duration};
    #[test]
    fn native_ipv4_source_and_split_payloads() {
        let tx = UdpSocket::bind("0.0.0.0:0").unwrap();
        let rx = UdpSocket::bind("127.0.0.1:0").unwrap();
        rx.set_read_timeout(Some(Duration::from_secs(1))).unwrap();
        let mut addr = SOCKADDR_IN::default();
        addr.sin_family = AF_INET;
        addr.sin_port = rx.local_addr().unwrap().port().to_be();
        addr.sin_addr.S_un.S_addr = u32::from_ne_bytes([127, 0, 0, 1]);
        let h = [1, 2, 3];
        let data = [10, 11, 12, 13, 14, 15];
        let spans = [
            Span {
                data: data.as_ptr(),
                len: 3,
            },
            Span {
                data: data[3..].as_ptr(),
                len: 3,
            },
        ];
        let source = [127, 0, 0, 1];
        let mut b = Batch {
            socket: tx.as_raw_socket() as usize,
            target: (&addr as *const SOCKADDR_IN).cast(),
            target_len: 16,
            source: source.as_ptr(),
            source_v6: false,
            headers: h.as_ptr(),
            header_size: 1,
            payloads: spans.as_ptr(),
            spans: 2,
            unit: 2,
            offset: 1,
            count: 2,
        };
        // Windows versions without USO fall back to two ordinary datagrams.
        if !unsafe { pl_udp_send(&b) } {
            b.count = 1;
            assert!(unsafe { pl_udp_send(&b) });
            b.offset += 1;
            assert!(unsafe { pl_udp_send(&b) });
        }
        for expected in [[2, 12, 13], [3, 14, 15]] {
            let mut bytes = [0; 32];
            let (n, from) = rx.recv_from(&mut bytes).unwrap();
            assert_eq!(from.ip().to_string(), "127.0.0.1");
            assert_eq!(&bytes[..n], &expected);
        }
    }
}
