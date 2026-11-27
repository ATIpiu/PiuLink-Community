use std::{ffi::c_void, sync::Mutex};

#[derive(Default)]
struct Health {
    seen: Option<u32>,
    good: u32,
    received: u64,
    expected: u64,
    start: u64,
    first: u64,
    prior_loss: u64,
    poor: bool,
}
impl Health {
    fn observe(&mut self, id: u32, now: u64) -> i32 {
        let Some(last) = self.seen else {
            self.seen = Some(id);
            self.first = now;
            return -1;
        };
        let distance = id.wrapping_sub(last);
        if distance > i32::MAX as u32 {
            return -1;
        }
        self.seen = Some(id);
        if now.saturating_sub(self.first) < 3000 {
            return -1;
        }
        let mut change = -1;
        if now.saturating_sub(self.start) >= 3000 {
            if self.expected != 0 {
                let loss = 100u64.saturating_sub(self.received.saturating_mul(100) / self.expected);
                if !self.poor && (loss >= 30 || loss >= 15 && self.prior_loss >= 15) {
                    self.poor = true;
                    change = 1;
                } else if self.poor && loss <= 5 {
                    self.poor = false;
                    change = 0;
                }
                self.prior_loss = loss;
            }
            self.start = now;
            self.expected = 0;
            self.received = 0;
        }
        self.expected = self.expected.saturating_add(u64::from(distance));
        change
    }
}
static HEALTH: std::sync::LazyLock<Mutex<Health>> =
    std::sync::LazyLock::new(|| Mutex::new(Health::default()));
fn health() -> std::sync::MutexGuard<'static, Health> {
    HEALTH.lock().unwrap_or_else(|p| p.into_inner())
}
#[no_mangle]
pub extern "C" fn pl_health_reset() {
    *health() = Health::default();
}
#[no_mangle]
pub extern "C" fn pl_health_complete(id: u32) {
    let mut h = health();
    h.good = id;
    h.received = h.received.saturating_add(1);
}
#[no_mangle]
pub extern "C" fn pl_health_seen(id: u32, now: u64) -> i32 {
    health().observe(id, now)
}
#[no_mangle]
pub extern "C" fn pl_latest_frame() -> u32 {
    health().seen.unwrap_or(0)
}

fn legacy_report(good: u32) -> [u8; 32] {
    let mut b = [0; 32];
    b[4..8].copy_from_slice(&50u32.to_le_bytes());
    b[8..12].copy_from_slice(&1000u32.to_le_bytes());
    b[12..20].copy_from_slice(&u64::from(good).to_le_bytes());
    b[28..].copy_from_slice(&20u32.to_le_bytes());
    b
}
#[repr(C)]
pub struct FeedbackOps {
    stopped: extern "C" fn() -> bool,
    fec: extern "C" fn() -> i32,
    send: extern "C" fn(u16, *const u8, u16, bool) -> bool,
    sleep: extern "C" fn(u32),
}
#[no_mangle]
pub extern "C" fn pl_loss_feedback(
    periodic: bool,
    sunshine: bool,
    loss_type: u16,
    ops: &FeedbackOps,
) -> bool {
    let ping = [4, 0, 0, 0, 0, 0, 0, 0];
    while !(ops.stopped)() {
        if periodic && sunshine {
            loop {
                match (ops.fec)() {
                    0 => break,
                    1 => (),
                    _ => return false,
                }
            }
        }
        let sent = if periodic {
            (ops.send)(0x0200, ping.as_ptr(), 8, true)
        } else {
            let report = legacy_report(health().good);
            (ops.send)(loss_type, report.as_ptr(), 32, false)
        };
        if !sent {
            return false;
        }
        (ops.sleep)(if periodic { 100 } else { 50 });
    }
    true
}

fn ref_range(first: u32, last: u32) -> [u8; 24] {
    let mut b = [0; 24];
    b[..8].copy_from_slice(&u64::from(first).to_le_bytes());
    b[8..16].copy_from_slice(&u64::from(last).to_le_bytes());
    b
}
#[no_mangle]
pub unsafe extern "C" fn pl_request_reference(
    idr: bool,
    explicit: bool,
    first: u32,
    last: u32,
    idr_type: u16,
    inval_type: u16,
    payload: *const c_void,
    payload_len: u16,
    send: extern "C" fn(u16, *const c_void, u16) -> bool,
) -> bool {
    if idr && explicit {
        return send(idr_type, payload, payload_len);
    }
    let (first, last) = if idr {
        let last = pl_latest_frame();
        (last.saturating_sub(32), last)
    } else {
        (first, last)
    };
    let bytes = ref_range(first, last);
    send(inval_type, bytes.as_ptr().cast(), 24)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn loss_hysteresis_wrap_and_stale_reports() {
        let mut h = Health::default();
        let mut id = u32::MAX - 1;
        h.observe(id, 100);
        id = id.wrapping_add(3);
        assert_eq!(h.observe(id, 3100), -1);
        assert_eq!(h.observe(id.wrapping_sub(1), 3200), -1);
        h.expected = 100;
        h.received = 84;
        assert_eq!(h.observe(id + 1, 6100), -1);
        h.expected = 100;
        h.received = 85;
        assert_eq!(h.observe(id + 2, 9100), 1);
        h.expected = 100;
        h.received = 95;
        assert_eq!(h.observe(id + 3, 12100), 0);
        h.expected = 100;
        h.received = 70;
        assert_eq!(h.observe(id + 4, 15100), 1);
    }
    #[test]
    fn legacy_reports_are_little_endian_and_reserved_bytes_zero() {
        let report = legacy_report(0xfedcba98);
        assert_eq!(&report[12..20], &[0x98, 0xba, 0xdc, 0xfe, 0, 0, 0, 0]);
        assert_eq!(&report[20..28], &[0; 8]);
        let range = ref_range(21, 53);
        assert_eq!((range[0], range[8]), (21, 53));
        assert_eq!(&range[16..], &[0; 8]);
    }
}
