use std::ffi::c_void;
use windows::{core::Interface, Win32::Graphics::Dxgi::*};

const OK: i32 = 0;
const REINIT: i32 = 1;
const TIMEOUT: i32 = 2;
const ERROR: i32 = 4;
fn acquire_status(hr: i32) -> i32 {
    if hr == 0 {
        OK
    } else if hr == DXGI_ERROR_WAIT_TIMEOUT.0 {
        TIMEOUT
    } else if hr == 128 || hr == DXGI_ERROR_ACCESS_LOST.0 || hr == DXGI_ERROR_ACCESS_DENIED.0 {
        REINIT
    } else {
        ERROR
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_dxgi_release(raw: *mut c_void, held: &mut bool, last: &mut i32) -> i32 {
    if !std::mem::take(held) {
        *last = 0;
        return OK;
    }
    let Some(dup) = IDXGIOutputDuplication::from_raw_borrowed(&raw) else {
        return ERROR;
    };
    *last = (dup.vtable().ReleaseFrame)(raw).0;
    if *last == 0 || *last == DXGI_ERROR_INVALID_CALL.0 {
        OK
    } else if *last == DXGI_ERROR_ACCESS_LOST.0 {
        REINIT
    } else {
        ERROR
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_dxgi_acquire(
    raw: *mut c_void,
    held: &mut bool,
    timeout: u32,
    info: *mut DXGI_OUTDUPL_FRAME_INFO,
    resource: *mut *mut c_void,
    last: &mut i32,
) -> i32 {
    let release = pl_dxgi_release(raw, held, last);
    if release != OK {
        return release;
    }
    let Some(dup) = IDXGIOutputDuplication::from_raw_borrowed(&raw) else {
        return ERROR;
    };
    if info.is_null() || resource.is_null() {
        return ERROR;
    }
    *last = (dup.vtable().AcquireNextFrame)(raw, timeout, info, resource).0;
    let status = acquire_status(*last);
    *held = status == OK;
    status
}
#[repr(C)]
#[derive(Copy, Clone, PartialEq, Debug)]
pub struct Rate {
    num: u32,
    den: u32,
}
#[no_mangle]
pub extern "C" fn pl_capture_rate(strict: Rate, screen: Rate, rounded: u32, fps: u32) -> Rate {
    if strict.num > 0 && strict.den > 0 {
        return strict;
    }
    let base = Rate {
        num: fps.max(1),
        den: 1,
    };
    if rounded == 0 || fps == 0 || screen.den <= 1 {
        return base;
    }
    let mut candidate = screen;
    if fps % rounded == 0 {
        let Some(n) = candidate.num.checked_mul(fps / rounded) else {
            return base;
        };
        candidate.num = n;
    } else if rounded % fps == 0 {
        let Some(d) = candidate.den.checked_mul(rounded / fps) else {
            return base;
        };
        candidate.den = d;
    }
    let ratio = f64::from(candidate.num) / f64::from(candidate.den) / f64::from(fps);
    if ratio > 0.99 && ratio < 1.0 {
        candidate
    } else {
        base
    }
}
fn deadline(start: i64, count: u32, rate: Rate) -> Option<i64> {
    let nanos = i128::from(count) * i128::from(rate.den) * 1_000_000_000 / i128::from(rate.num);
    i64::try_from(i128::from(start) + nanos).ok()
}
#[repr(C)]
pub struct CaptureOps {
    current: extern "C" fn(*mut c_void) -> bool,
    now: extern "C" fn() -> i64,
    wait: extern "C" fn(*mut c_void, i64),
    take: extern "C" fn(*mut c_void, i32, &mut i64, &mut bool) -> i32,
    push: extern "C" fn(*mut c_void, bool) -> bool,
    release: extern "C" fn(*mut c_void) -> i32,
}
#[no_mangle]
pub extern "C" fn pl_capture_run(ctx: *mut c_void, rate: Rate, ops: &CaptureOps) -> i32 {
    if rate.num == 0 || rate.den == 0 {
        return ERROR;
    }
    let mut group = None;
    let mut frames = 0;
    loop {
        if !(ops.current)(ctx) {
            return REINIT;
        }
        let mut stamp = 0;
        let mut image = false;
        let mut status = TIMEOUT;
        if let Some(due) = group
            .and_then(|start| deadline(start, frames, rate))
            .filter(|due| *due > (ops.now)())
        {
            (ops.wait)(ctx, due);
            status = (ops.take)(ctx, 0, &mut stamp, &mut image);
            if status == OK && image {
                frames = frames.saturating_add(1)
            } else {
                group = None
            }
        } else {
            group = None
        }
        if status == TIMEOUT || (status == OK && group.is_none()) {
            status = (ops.take)(ctx, 200, &mut stamp, &mut image);
            if status == OK && image {
                group = Some(stamp);
                frames = 1;
            } else if status == TIMEOUT {
                std::thread::sleep(std::time::Duration::from_millis(10));
            }
        }
        if status != OK && status != TIMEOUT {
            return status;
        }
        if !(ops.push)(ctx, status == OK) {
            return OK;
        }
        let released = (ops.release)(ctx);
        if released != OK {
            return released;
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn capture_fractional_rates_and_long_group_deadlines() {
        let no = Rate { num: 0, den: 0 };
        let ntsc = Rate {
            num: 60000,
            den: 1001,
        };
        assert_eq!(pl_capture_rate(no, ntsc, 60, 60), ntsc);
        assert_eq!(
            pl_capture_rate(no, ntsc, 60, 30),
            Rate {
                num: 60000,
                den: 2002
            }
        );
        assert_eq!(
            pl_capture_rate(Rate { num: 120, den: 1 }, ntsc, 60, 30),
            Rate { num: 120, den: 1 }
        );
        assert_eq!(deadline(125, 60000, ntsc), Some(1001000000125));
        for (hr, status) in [
            (0, OK),
            (DXGI_ERROR_WAIT_TIMEOUT.0, TIMEOUT),
            (DXGI_ERROR_ACCESS_LOST.0, REINIT),
            (128, REINIT),
            (-1, ERROR),
        ] {
            assert_eq!(acquire_status(hr), status)
        }
    }
}
