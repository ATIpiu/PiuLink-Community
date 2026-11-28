use std::ffi::c_void;

type Driver = unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32;
#[repr(C)]
pub struct Resources {
    encoder: *mut c_void,
    config: *mut c_void,
    event: *mut c_void,
    buffer: *mut c_void,
    initialize: Driver,
    register_event: Driver,
    create_buffer: Driver,
    unregister_event: Driver,
}
#[repr(C)]
pub struct ResourceResult {
    stage: i32,
    error: i32,
    rollback_error: i32,
}
#[no_mangle]
pub unsafe extern "C" fn pl_nv_initialize(r: &Resources) -> ResourceResult {
    for (stage, f, data) in [
        (1, r.initialize, r.config),
        (2, r.register_event, r.event),
        (3, r.create_buffer, r.buffer),
    ] {
        if data.is_null() && stage == 2 {
            continue;
        }
        let error = f(r.encoder, data);
        if error != 0 {
            let rollback_error = if stage == 3 && !r.event.is_null() {
                (r.unregister_event)(r.encoder, r.event)
            } else {
                0
            };
            return ResourceResult {
                stage,
                error,
                rollback_error,
            };
        }
    }
    ResourceResult {
        stage: 0,
        error: 0,
        rollback_error: 0,
    }
}

#[repr(C)]
pub struct NvPlan {
    bitrate: u32,
    vbv: u32,
    refs: u32,
    rows: u32,
    cols: u32,
    rfi: bool,
}
#[no_mangle]
pub extern "C" fn pl_nv_plan(
    codec: i32,
    kbps: i32,
    fps: i32,
    vbv_increase: i32,
    requested_refs: i32,
    multiple: bool,
    invalidation: bool,
    slices: i32,
    plan: &mut NvPlan,
) -> bool {
    if !(0..=2).contains(&codec) || kbps <= 0 || fps <= 0 {
        return false;
    }
    let Some(bitrate) = (kbps as u32).checked_mul(1000) else {
        return false;
    };
    let base = bitrate as u64 / fps as u64;
    let vbv = base + base * vbv_increase.max(0) as u64 / 100;
    let Ok(vbv) = u32::try_from(vbv) else {
        return false;
    };
    let refs = if requested_refs > 0 {
        requested_refs as u32
    } else if codec == 2 {
        8
    } else {
        5
    };
    let power = 32 - (slices.max(1) as u32).leading_zeros() - 1;
    // Half-integer log2 is rounded in opposite directions for rows and columns.
    let high = power + u32::from(!(slices.max(1) as u32).is_power_of_two());
    *plan = NvPlan {
        bitrate,
        vbv,
        refs: if multiple { refs } else { 1 },
        rfi: invalidation && multiple,
        rows: 1 << high.div_ceil(2),
        cols: 1 << (power / 2),
    };
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn av1_tiles_refs_and_vbv() {
        let mut p = NvPlan {
            bitrate: 0,
            vbv: 0,
            refs: 0,
            rows: 0,
            cols: 0,
            rfi: false,
        };
        for n in 1..=64 {
            assert!(pl_nv_plan(2, 21000, 60, 20, 0, true, true, n, &mut p));
            let half = (n as f64).log2() / 2.;
            assert_eq!(
                (p.rows, p.cols),
                (2u32.pow(half.ceil() as u32), 2u32.pow(half.floor() as u32))
            );
        }
        assert_eq!(
            (p.bitrate, p.vbv, p.refs, p.rfi),
            (21000000, 420000, 8, true)
        );
    }
}

#[cfg(test)]
mod lifecycle {
    use super::*;
    use std::cell::RefCell;
    thread_local! { static CALLS: RefCell<Vec<usize>> = const { RefCell::new(Vec::new()) }; }
    unsafe extern "system" fn execute(_: *mut c_void, p: *mut c_void) -> i32 {
        CALLS.with(|c| c.borrow_mut().push(p as usize));
        if p as usize == 3 {
            8
        } else {
            0
        }
    }
    unsafe extern "system" fn rollback(_: *mut c_void, _: *mut c_void) -> i32 {
        CALLS.with(|c| c.borrow_mut().push(4));
        0
    }
    #[test]
    fn rolls_back_only_acquired_async_events() {
        let mut r = Resources {
            encoder: 1usize as _,
            config: 1usize as _,
            event: 2usize as _,
            buffer: 3usize as _,
            initialize: execute,
            register_event: execute,
            create_buffer: execute,
            unregister_event: rollback,
        };
        for (event, expected) in [(2usize, vec![1, 2, 3, 4]), (0, vec![1, 3]), (3, vec![1, 3])] {
            r.event = event as _;
            CALLS.with(|c| c.borrow_mut().clear());
            let result = unsafe { pl_nv_initialize(&r) };
            assert_eq!(result.error, 8);
            CALLS.with(|c| assert_eq!(*c.borrow(), expected));
        }
    }
}
