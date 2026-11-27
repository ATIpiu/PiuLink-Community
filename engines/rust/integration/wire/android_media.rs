use super::audio_queue::{pl_audio_queue_add, pl_audio_queue_pop, Queue};
use std::{
    ffi::c_void,
    ptr, slice,
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};

const AUDIO: usize = 0;
const VIDEO: usize = 1;
static SEEN: [AtomicBool; 2] = [AtomicBool::new(false), AtomicBool::new(false)];
static KEYFRAME: AtomicBool = AtomicBool::new(false);
#[no_mangle]
pub extern "C" fn pl_media_reset(video: bool) {
    SEEN[usize::from(video)].store(false, Ordering::Release);
    if video {
        KEYFRAME.store(false, Ordering::Release);
    }
}
#[no_mangle]
pub extern "C" fn pl_media_seen(video: bool) -> bool {
    SEEN[usize::from(video)].load(Ordering::Acquire)
}
#[no_mangle]
pub extern "C" fn pl_media_keyframe() {
    KEYFRAME.store(true, Ordering::Release);
}

#[repr(C)]
struct PollFd {
    fd: i32,
    events: i16,
    revents: i16,
}
extern "C" {
    fn recv(fd: i32, data: *mut u8, n: usize, flags: i32) -> isize;
    fn sendto(
        fd: i32,
        data: *const u8,
        n: usize,
        flags: i32,
        addr: *const c_void,
        addr_len: u32,
    ) -> isize;
    fn poll(fds: *mut PollFd, count: u32, timeout: i32) -> i32;
    fn __errno() -> *mut i32;
    fn malloc(n: usize) -> *mut c_void;
    fn free(data: *mut c_void);
}
extern "C" fn allocate(n: usize) -> *mut c_void {
    unsafe { malloc(n) }
}
struct Allocation(*mut u8);
impl Allocation {
    fn ensure(&mut self, n: usize) -> Result<*mut u8, i32> {
        if self.0.is_null() {
            self.0 = allocate(n).cast();
        }
        if self.0.is_null() {
            Err(-1)
        } else {
            Ok(self.0)
        }
    }
    fn transfer(&mut self) {
        self.0 = ptr::null_mut();
    }
}
impl Drop for Allocation {
    fn drop(&mut self) {
        unsafe { free(self.0.cast()) }
    }
}
unsafe fn read(fd: i32, data: *mut u8, len: usize, poll_first: bool) -> Result<usize, i32> {
    loop {
        if poll_first {
            let mut fdinfo = PollFd {
                fd,
                events: 1,
                revents: 0,
            };
            match poll(&mut fdinfo, 1, 100) {
                0 => return Ok(0),
                -1 => {
                    let err = *__errno();
                    return if err == 4 { Ok(0) } else { Err(err) };
                }
                _ => {}
            }
        }
        let n = recv(fd, data, len, 0);
        if n >= 0 {
            return Ok(n as usize);
        }
        match *__errno() {
            111 => continue,
            4 | 11 | 110 => return Ok(0),
            e => return Err(e),
        }
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_media_ping(
    fd: i32,
    addr: *const c_void,
    addr_len: u32,
    id: *const u8,
    interrupted: extern "C" fn() -> bool,
    wait: extern "C" fn(),
) {
    if id.is_null() || addr.is_null() {
        return;
    }
    let id = slice::from_raw_parts(id, 16);
    let mut ping = [0u8; 20];
    ping[..16].copy_from_slice(id);
    let mut seq = 0u32;
    while !interrupted() {
        let bytes = if id[0] == 0 {
            &b"PING"[..]
        } else {
            seq = seq.wrapping_add(1);
            ping[16..].copy_from_slice(&seq.to_be_bytes());
            &ping[..]
        };
        // ICMP errors on pings before SETUP completes are handled by the receive path.
        sendto(fd, bytes.as_ptr(), bytes.len(), 0, addr, addr_len);
        wait();
    }
}

fn native_rtp(header: &mut [u8]) {
    let seq = u16::from_be_bytes([header[2], header[3]]);
    let ts = u32::from_be_bytes([header[4], header[5], header[6], header[7]]);
    let ssrc = u32::from_be_bytes([header[8], header[9], header[10], header[11]]);
    header[2..4].copy_from_slice(&seq.to_ne_bytes());
    header[4..8].copy_from_slice(&ts.to_ne_bytes());
    header[8..12].copy_from_slice(&ssrc.to_ne_bytes());
}
#[repr(C)]
pub struct VideoBackend {
    interrupted: extern "C" fn() -> bool,
    current: extern "C" fn() -> u32,
    queue: extern "C" fn(*mut u8, i32, *mut u8) -> bool,
}
#[no_mangle]
pub unsafe extern "C" fn pl_receive_video(
    fd: i32,
    cap: i32,
    entry: usize,
    key: *const u8,
    poll_first: bool,
    ops: &VideoBackend,
) -> i32 {
    if !(12..=65535).contains(&cap) {
        return -1;
    }
    let cap = cap as usize;
    let Some(total) = cap.checked_add(entry) else {
        return -1;
    };
    let mut packet = Allocation(ptr::null_mut());
    let cipher = if key.is_null() {
        None
    } else {
        super::video_cipher::VideoCipher::new(slice::from_raw_parts(key, 16))
    };
    let encrypted = cipher.is_some();
    let mut ciphertext = if encrypted {
        vec![0u8; cap + 32]
    } else {
        Vec::new()
    };
    let mut start = Instant::now();
    while !(ops.interrupted)() {
        let buf = match packet.ensure(total) {
            Ok(v) => v,
            Err(e) => return e,
        };
        let (dst, limit) = if encrypted {
            (ciphertext.as_mut_ptr(), ciphertext.len())
        } else {
            (buf, cap)
        };
        let mut n = match read(fd, dst, limit, poll_first) {
            Ok(n) => n,
            Err(e) => return e,
        };
        if n != 0 && !SEEN[VIDEO].swap(true, Ordering::AcqRel) {
            start = Instant::now();
        }
        if !KEYFRAME.load(Ordering::Acquire) && start.elapsed() >= Duration::from_secs(10) {
            return if pl_media_seen(true) { -101 } else { -100 };
        }
        if n < 12 + if encrypted { 32 } else { 0 } {
            continue;
        }
        if encrypted {
            let hinted = u32::from_le_bytes([
                ciphertext[12],
                ciphertext[13],
                ciphertext[14],
                ciphertext[15],
            ]);
            if hinted != 0 && hinted < (ops.current)() {
                continue;
            }
            if !cipher.as_ref().unwrap().open(&mut ciphertext[..n]) {
                continue;
            }
            n -= 32;
            ptr::copy_nonoverlapping(ciphertext.as_ptr().add(32), buf, n);
        }
        native_rtp(slice::from_raw_parts_mut(buf, 12));
        if (ops.queue)(buf, n as i32, buf.add(cap)) {
            packet.transfer();
        }
    }
    0
}

#[repr(C)]
pub struct AudioBackend {
    interrupted: extern "C" fn() -> bool,
    push: extern "C" fn(*mut u8, u16) -> i32,
    now: extern "C" fn() -> u64,
}
#[no_mangle]
pub unsafe extern "C" fn pl_receive_audio(
    fd: i32,
    cap: u16,
    prefix: u16,
    duration: u32,
    poll_first: bool,
    queue: &mut Queue,
    ops: &AudioBackend,
) -> i32 {
    if cap < 12 || duration == 0 {
        return -1;
    }
    let mut packet = Allocation(ptr::null_mut());
    let mut discard = 500 / duration;
    while !(ops.interrupted)() {
        let base = match packet.ensure(usize::from(cap) + usize::from(prefix)) {
            Ok(p) => p,
            Err(e) => return e,
        };
        let buf = base.add(prefix as usize);
        let n = match read(fd, buf, cap as usize, poll_first) {
            Ok(n) => n,
            Err(e) => return e,
        };
        if n == 0 {
            if pl_media_seen(false) {
                discard = 0
            }
            continue;
        }
        if n < 12 {
            continue;
        }
        SEEN[AUDIO].store(true, Ordering::Release);
        let header = slice::from_raw_parts_mut(buf, 12);
        if discard != 0 {
            if header[1] == 97 {
                discard -= 1
            }
            continue;
        }
        native_rtp(header);
        let status = pl_audio_queue_add(queue, buf, n as u16, duration, (ops.now)());
        if status == 4 {
            match (ops.push)(base, n as u16) {
                1 => packet.transfer(),
                0 => {}
                _ => return 0,
            }
        } else if status & 2 != 0 {
            loop {
                let mut len = 0;
                let mut out =
                    Allocation(pl_audio_queue_pop(queue, prefix, allocate, &mut len).cast());
                if out.0.is_null() {
                    break;
                }
                match (ops.push)(out.0, len) {
                    1 => out.transfer(),
                    0 => {}
                    _ => return 0,
                }
            }
        }
    }
    0
}
