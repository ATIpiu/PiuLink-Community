use std::ffi::{c_char, c_void, CStr};
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};

const LAST: u8 = 11;
const START: u8 = 0;
const COMPLETE: u8 = 1;
const FAILED: u8 = 2;
struct Lifecycle {
    acquired: AtomicU8,
    interrupted: AtomicBool,
    terminated: AtomicBool,
}
impl Lifecycle {
    const fn new() -> Self {
        Self {
            acquired: AtomicU8::new(0),
            interrupted: AtomicBool::new(false),
            terminated: AtomicBool::new(false),
        }
    }
    fn start(&self, mut run: impl FnMut(u8) -> i32, mut notify: impl FnMut(u8, u8, i32)) -> i32 {
        debug_assert_eq!(self.acquired.load(Ordering::Acquire), 0);
        for stage in 1..=LAST {
            notify(stage, START, 0);
            let error = if self.interrupted.load(Ordering::Acquire) {
                -1
            } else {
                run(stage)
            };
            if error != 0 {
                notify(stage, FAILED, error);
                return error;
            }
            self.acquired.store(stage, Ordering::Release);
            notify(stage, COMPLETE, 0);
        }
        0
    }
    fn reset(&self) {
        self.interrupted.store(false, Ordering::Release);
        self.terminated.store(false, Ordering::Release);
    }
    fn stop(&self, mut release: impl FnMut(u8)) {
        self.terminated.store(true, Ordering::Release);
        self.interrupted.store(true, Ordering::Release);
        // Callers serialize start and stop as required by LiStartConnection.
        // Claim the entire list before invoking backends so a repeated stop is harmless.
        let acquired = self.acquired.swap(0, Ordering::AcqRel);
        for stage in (1..=acquired).rev() {
            release(stage);
        }
    }
    fn claim_termination(&self) -> bool {
        !self.interrupted.load(Ordering::Acquire)
            && self
                .terminated
                .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
    }
}
static SESSION: Lifecycle = Lifecycle::new();
#[no_mangle]
pub extern "C" fn pl_connection_reset() {
    SESSION.reset();
}
#[no_mangle]
pub extern "C" fn pl_connection_interrupt() {
    SESSION.interrupted.store(true, Ordering::Release);
}
#[no_mangle]
pub extern "C" fn pl_connection_interrupted() -> bool {
    SESSION.interrupted.load(Ordering::Acquire)
}
#[no_mangle]
pub extern "C" fn pl_connection_claim_termination() -> bool {
    SESSION.claim_termination()
}
#[no_mangle]
pub extern "C" fn pl_connection_start(
    ctx: *mut c_void,
    run: extern "C" fn(*mut c_void, u8) -> i32,
    notify: extern "C" fn(u8, u8, i32),
) -> i32 {
    SESSION.start(|s| run(ctx, s), |s, n, e| notify(s, n, e))
}
#[no_mangle]
pub extern "C" fn pl_connection_stop(release: extern "C" fn(u8)) {
    SESSION.stop(|s| release(s));
}
#[no_mangle]
pub extern "C" fn pl_resolve_host(
    address: *const c_char,
    port: u16,
    attempt: extern "C" fn(*const c_char, u16) -> i32,
    delay: extern "C" fn(i32),
) -> i32 {
    if address.is_null() || port == 0 {
        return -1;
    }
    if port != 48010 {
        let err = attempt(address, port);
        if err == 0 {
            return 0;
        }
        if pl_connection_interrupted() {
            return err;
        }
        delay(1000);
        if pl_connection_interrupted() {
            return err;
        }
        return attempt(address, port);
    }
    let mut last = -1;
    for port in [47984, 47989, 48010] {
        if pl_connection_interrupted() {
            break;
        }
        last = attempt(address, port);
        if last == 0 {
            break;
        }
    }
    last
}
#[no_mangle]
pub unsafe extern "C" fn pl_rtsp_url_port(value: *const c_char) -> u16 {
    if value.is_null() {
        return 48010;
    }
    let url = CStr::from_ptr(value).to_bytes();
    // A colon inside a bracketed IPv6 host is not a port delimiter.
    let end = url
        .windows(3)
        .position(|w| w == b"://")
        .map_or(0, |i| i + 3);
    let host = url[end..]
        .split(|b| matches!(*b, b'/' | b'?' | b'#'))
        .next()
        .unwrap_or_default();
    let suffix = if host.first() == Some(&b'[') {
        host.iter()
            .position(|&b| b == b']')
            .and_then(|i| host[i + 1..].strip_prefix(b":"))
    } else {
        host.iter().position(|&b| b == b':').map(|i| &host[i + 1..])
    };
    suffix
        .and_then(|s| std::str::from_utf8(s).ok())
        .and_then(|s| s.parse::<u16>().ok())
        .filter(|p| *p != 0)
        .unwrap_or(48010)
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn every_stage_unwinds_only_acquired_resources() {
        for failed in 1..=LAST + 1 {
            let session = Lifecycle::new();
            let mut notifications = vec![];
            let result = session.start(
                |s| if s == failed { 135 } else { 0 },
                |s, n, e| notifications.push((s, n, e)),
            );
            assert_eq!(result, if failed <= LAST { 135 } else { 0 });
            if failed <= LAST {
                assert_eq!(notifications.last(), Some(&(failed, FAILED, 135)));
            }
            let mut released = vec![];
            session.stop(|s| released.push(s));
            session.stop(|_| panic!("duplicate release"));
            assert_eq!(released, (1..failed).rev().collect::<Vec<_>>());
            session.reset();
            assert_eq!(session.start(|_| 0, |_, _, _| {}), 0);
        }
    }
    #[test]
    fn interruption_and_termination_are_latched() {
        let session = Lifecycle::new();
        assert!(session.claim_termination());
        assert!(!session.claim_termination());
        session.reset();
        let ret = session.start(
            |s| {
                if s == 5 {
                    session.interrupted.store(true, Ordering::Release);
                }
                0
            },
            |_, _, _| {},
        );
        assert_eq!(ret, -1);
        let mut released = vec![];
        session.stop(|s| released.push(s));
        assert_eq!(released, vec![5, 4, 3, 2, 1]);
        assert!(!session.claim_termination());
    }
    #[test]
    fn bracketed_ipv6_and_custom_rtsp_ports() {
        for (s, p) in [
            ("rtsp://[::1]:49910/a", 49910),
            ("rtspenc://[::1]/p", 48010),
            ("rtsp://host:444?key=1", 444),
            ("rtsp://host:65536/", 48010),
        ] {
            let s = std::ffi::CString::new(s).unwrap();
            assert_eq!(unsafe { pl_rtsp_url_port(s.as_ptr()) }, p);
        }
    }
}
