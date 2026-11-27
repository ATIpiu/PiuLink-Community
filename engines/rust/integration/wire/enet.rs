use std::ffi::c_void;
type Call = unsafe extern "C" fn(*mut c_void, i32, u32) -> i32;

// The ENet service timeout is sliced so the library can retransmit and the app can cancel.
#[no_mangle]
pub unsafe extern "C" fn pl_enet_service(
    ctx: *mut c_void,
    mut timeout: u32,
    slice: u32,
    call: Call,
) -> i32 {
    if slice == 0 {
        return -1;
    }
    loop {
        let wait = timeout.min(slice);
        let result = call(ctx, 0, wait);
        if result != 0 || timeout == 0 {
            return result;
        }
        timeout -= wait;
    }
}

#[no_mangle]
pub extern "C" fn pl_enet_wait(next: u32, now: u32, ping: u32) -> u32 {
    let delta = next.wrapping_sub(now);
    if delta >= 86_400_000 {
        10
    } else {
        (delta + 1).min(ping)
    }
}

// The ENet platform adapter owns sockets and peers. Release strictly in reverse order on failure.
#[no_mangle]
pub unsafe extern "C" fn pl_enet_connect(ctx: *mut c_void, timeout: u32, call: Call) -> i32 {
    if call(ctx, 0, 0) == 0 {
        return -1;
    }
    if call(ctx, 1, 0) == 0 {
        call(ctx, 5, 0);
        return -1;
    }
    let err = call(ctx, 2, timeout);
    if err != 0 {
        call(ctx, 4, 0);
        call(ctx, 5, 0);
        return err;
    }
    call(ctx, 3, 0);
    0
}

#[no_mangle]
pub extern "C" fn pl_enet_dispatch(kind: i32, authorized: bool, running: bool, len: usize) -> i32 {
    if !authorized {
        return 4;
    }
    match kind {
        1 => 1,             // connect
        2 if running => 2,  // stop
        2 => 5,             // already stopped
        3 if len >= 2 => 3, // payload after uint16_le type
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ack_wait_and_short_payload() {
        assert_eq!(pl_enet_wait(2, u32::MAX, 20), 4);
        assert_eq!(pl_enet_wait(20, 21, 800), 10);
        assert_eq!(pl_enet_dispatch(3, true, true, 1), 0);
        assert_eq!(pl_enet_dispatch(3, false, true, 100), 4);
    }
    #[test]
    fn handshake_error_releases_both_handles() {
        unsafe extern "C" fn op(ctx: *mut c_void, step: i32, _: u32) -> i32 {
            (*(ctx as *mut Vec<i32>)).push(step);
            if step == 2 {
                110
            } else {
                1
            }
        }
        let mut steps = Vec::<i32>::new();
        assert_eq!(
            unsafe { pl_enet_connect((&mut steps as *mut Vec<i32>).cast(), 1000, op) },
            110
        );
        assert_eq!(steps, [0, 1, 2, 4, 5]);
    }
}
