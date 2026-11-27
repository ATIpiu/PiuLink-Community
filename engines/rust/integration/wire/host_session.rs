use std::sync::{
    atomic::{AtomicU32, Ordering},
    Condvar, Mutex, MutexGuard,
};
#[derive(Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
enum Phase {
    Stopped,
    Stopping,
    Starting,
    Running,
}
use self::Phase::*;
pub struct HostSession {
    phase: Mutex<Phase>,
    published: Condvar,
}
static ACTIVE: AtomicU32 = AtomicU32::new(0);
impl HostSession {
    fn new() -> Self {
        Self {
            phase: Mutex::new(Stopped),
            published: Condvar::new(),
        }
    }
    fn lock(&self) -> MutexGuard<'_, Phase> {
        self.phase.lock().unwrap_or_else(|p| p.into_inner())
    }
    fn wait(&self) -> MutexGuard<'_, Phase> {
        self.published
            .wait_while(self.lock(), |p| *p == Starting)
            .unwrap_or_else(|p| p.into_inner())
    }
}
#[no_mangle]
pub extern "C" fn pl_host_new() -> *mut HostSession {
    Box::into_raw(Box::new(HostSession::new()))
}
#[no_mangle]
pub unsafe extern "C" fn pl_host_free(session: *mut HostSession) {
    if !session.is_null() {
        drop(Box::from_raw(session))
    }
}
#[no_mangle]
pub extern "C" fn pl_host_begin(s: &HostSession) -> bool {
    let mut phase = s.lock();
    if *phase != Stopped {
        return false;
    }
    *phase = Starting;
    true
}
#[no_mangle]
pub extern "C" fn pl_host_publish(s: &HostSession, success: bool) -> bool {
    let mut phase = s.lock();
    if *phase != Starting {
        return false;
    }
    *phase = if success { Running } else { Stopped };
    let first = success && ACTIVE.fetch_add(1, Ordering::AcqRel) == 0;
    s.published.notify_all();
    first
}
#[no_mangle]
pub extern "C" fn pl_host_wait(s: &HostSession) -> bool {
    *s.wait() == Running
}
#[no_mangle]
pub extern "C" fn pl_host_state(s: &HostSession) -> i32 {
    *s.lock() as i32
}
#[no_mangle]
pub extern "C" fn pl_host_stop(s: &HostSession) -> bool {
    let mut phase = s.wait();
    if *phase != Running {
        return false;
    }
    *phase = Stopping;
    true
}
#[no_mangle]
pub extern "C" fn pl_host_finish(s: &HostSession) -> bool {
    let mut phase = s.lock();
    if *phase != Stopping {
        return false;
    }
    *phase = Stopped;
    ACTIVE.fetch_sub(1, Ordering::AcqRel) == 1
}
#[no_mangle]
pub extern "C" fn pl_host_active() -> u32 {
    ACTIVE.load(Ordering::Acquire)
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::{sync::Arc, thread};
    #[test]
    fn concurrent_workers_start_stop_and_rollback() {
        let a = Arc::new(HostSession::new());
        let b = HostSession::new();
        assert!(pl_host_begin(&a));
        assert!(!pl_host_begin(&a));
        let waiter = {
            let a = a.clone();
            thread::spawn(move || pl_host_wait(&a))
        };
        assert!(!pl_host_publish(&a, false));
        assert!(!waiter.join().unwrap());
        assert_eq!(pl_host_active(), 0);
        assert!(pl_host_begin(&a));
        assert!(pl_host_publish(&a, true));
        assert!(pl_host_begin(&b));
        assert!(!pl_host_publish(&b, true));
        assert_eq!(pl_host_active(), 2);
        let threads: Vec<_> = (0..8)
            .map(|_| {
                let a = a.clone();
                thread::spawn(move || pl_host_stop(&a))
            })
            .collect();
        assert_eq!(
            threads
                .into_iter()
                .map(|t| u32::from(t.join().unwrap()))
                .sum::<u32>(),
            1
        );
        assert!(!pl_host_finish(&a));
        assert!(!pl_host_finish(&a));
        assert!(pl_host_stop(&b));
        assert!(pl_host_finish(&b));
        assert_eq!(pl_host_active(), 0);
    }
}
