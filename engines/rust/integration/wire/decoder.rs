use std::ffi::c_void;

const EXCEPTION: i32 = i32::MIN;

// The callback performs a single MediaCodec or Java renderer operation on the calling thread.
// A pending Java exception stops further JNI calls until control returns to Java.
fn configure(mut op: impl FnMut(i32, i32) -> i32) -> i32 {
    let mut attempt = 0;
    loop {
        // 1 = configured, 2 = another low-latency option, 3 = retry initial dimensions.
        match op(0, attempt) {
            1 => return 0,
            2 => attempt += 1,
            3 => {
                if op(1, 0) == EXCEPTION {
                    return EXCEPTION;
                }
                attempt = 0;
            }
            EXCEPTION => return EXCEPTION,
            _ => return -5,
        }
    }
}

fn recover(mut stage: i32, can_reset: bool, mut op: impl FnMut(i32, i32) -> i32) -> i32 {
    let mut counted = false;
    while (1..=4).contains(&stage) {
        if stage != 1 && !counted {
            if op(0, 0) == EXCEPTION {
                return EXCEPTION;
            }
            counted = true;
        }
        if stage == 3 && !can_reset {
            stage = 4;
        }
        match op(stage, 0) {
            0 => return 0,
            1 => stage += 1,
            other => return other, // invalid Surface or a pending renderer exception
        }
    }
    0
}

fn acquire(index: i32, mut op: impl FnMut(i32, i32) -> i32) -> i32 {
    let mut index = index;
    while index < 0 {
        if op(0, 0) != 0 {
            return -1;
        }
        index = op(1, 0);
        if index == EXCEPTION {
            return EXCEPTION;
        }
    }
    if op(2, index) == 1 {
        index
    } else {
        -1
    }
}

fn submit(mut op: impl FnMut(i32, i32) -> i32) -> i32 {
    match op(0, 0) {
        0 => {
            if op(1, 0) == EXCEPTION {
                return EXCEPTION;
            }
            1
        }
        1 => {
            op(2, 0);
            0
        } // transient failure, retain and clear dequeued buffer
        2 => {
            op(1, 0);
            0
        }
        _ => EXCEPTION,
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_codec_run(
    kind: i32,
    argument: i32,
    option: bool,
    ctx: *mut c_void,
    call: unsafe extern "C" fn(*mut c_void, i32, i32) -> i32,
) -> i32 {
    let op = |step, n| call(ctx, step, n);
    match kind {
        0 => configure(op),
        1 => recover(argument, option, op),
        2 => acquire(argument, op),
        3 => submit(op),
        _ => EXCEPTION,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fallback_restarts_options_for_original_size() {
        let mut steps = Vec::new();
        let mut results = [2, 3, 0, 1].into_iter();
        assert_eq!(
            configure(|op, n| {
                steps.push((op, n));
                results.next().unwrap()
            }),
            0
        );
        assert_eq!(steps, [(0, 0), (0, 1), (1, 0), (0, 0)]);
    }
    #[test]
    fn recovery_escalates_and_counts_once() {
        let mut calls = Vec::new();
        assert_eq!(
            recover(1, false, |step, _| {
                calls.push(step);
                i32::from(step != 4)
            }),
            0
        );
        assert_eq!(calls, [1, 0, 2, 4]);
        assert_eq!(recover(2, true, |step, _| if step == 2 { 2 } else { 0 }), 2);
    }
    #[test]
    fn buffer_retry_and_transient_ownership() {
        let mut calls = Vec::new();
        let mut results = [0, -1, 0, 9, 1].into_iter();
        assert_eq!(
            acquire(-1, |s, n| {
                calls.push((s, n));
                results.next().unwrap()
            }),
            9
        );
        assert_eq!(calls.last(), Some(&(2, 9)));
        let mut released = false;
        assert_eq!(
            submit(|s, _| {
                released |= s == 1;
                1
            }),
            0
        );
        assert!(!released);
    }
}
