use std::ffi::c_void;

// Each renderer owns a direct ByteBuffer. Its Java monitor serializes producer,
// Choreographer and codec-reset access. No codec index survives a reset.
#[repr(C)]
#[derive(Default)]
pub struct DisplayQueue {
    entries: [i32; 2],
    length: u32,
}
impl DisplayQueue {
    fn pop(&mut self) -> i32 {
        if self.length == 0 {
            return -1;
        }
        let first = self.entries[0];
        self.entries[0] = self.entries[1];
        self.length -= 1;
        first
    }
}
#[no_mangle]
pub extern "C" fn pl_balanced_frame(
    q: &mut DisplayQueue,
    op: i32,
    index: i32,
    now: i64,
    last: i64,
    fps: i32,
) -> i32 {
    match op {
        0 => {
            q.length = 0;
            -1
        }
        1 if index >= 0 => {
            let discard = if q.length == 2 { q.pop() } else { -1 };
            q.entries[q.length as usize] = index;
            q.length += 1;
            discard
        }
        2 if fps > 0 && now.saturating_sub(last) >= 800_000_000 / i64::from(fps) => q.pop(),
        _ => -1,
    }
}

#[repr(C)]
pub struct OutputOps {
    next: extern "C" fn(*mut c_void) -> i32,
    release: extern "C" fn(*mut c_void, i32, i32) -> bool,
}
#[no_mangle]
pub extern "C" fn pl_present_latest(
    ctx: *mut c_void,
    first: i32,
    keep: bool,
    ops: &OutputOps,
) -> i64 {
    let mut latest = first;
    let mut count = 1u32;
    loop {
        let index = (ops.next)(ctx);
        if index < 0 {
            if index == i32::MIN || !(ops.release)(ctx, latest, if keep { 1 } else { 2 }) {
                return -1;
            }
            return (i64::from(count) << 32) | i64::from(index as u32);
        }
        if !(ops.release)(ctx, latest, 0) {
            return -1;
        }
        latest = index;
        count += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn producer_drops_oldest_and_vsync_preserves_order() {
        let mut q = DisplayQueue::default();
        for (id, drop) in [(7, -1), (0, -1), (9, 7)] {
            assert_eq!(pl_balanced_frame(&mut q, 1, id, 0, 0, 0), drop);
        }
        assert_eq!(pl_balanced_frame(&mut q, 2, 0, 13_333_332, 0, 60), -1);
        assert_eq!(pl_balanced_frame(&mut q, 2, 0, 13_333_333, 0, 60), 0);
        assert_eq!(
            pl_balanced_frame(&mut q, 2, 0, 30_000_000, 13_333_333, 60),
            9
        );
        pl_balanced_frame(&mut q, 1, 8, 0, 0, 0);
        pl_balanced_frame(&mut q, 0, 0, 0, 0, 0);
        assert_eq!(pl_balanced_frame(&mut q, 2, 0, 100_000_000, 0, 60), -1);
    }
    #[test]
    fn draining_renders_only_latest_and_preserves_format_event() {
        struct Codec {
            idx: i32,
            released: Vec<(i32, i32)>,
        }
        extern "C" fn next(p: *mut c_void) -> i32 {
            let c = unsafe { &mut *p.cast::<Codec>() };
            c.idx += 1;
            if c.idx == 4 {
                -2
            } else {
                c.idx
            }
        }
        extern "C" fn release(p: *mut c_void, i: i32, how: i32) -> bool {
            unsafe { &mut *p.cast::<Codec>() }.released.push((i, how));
            true
        }
        for keep in [false, true] {
            let mut c = Codec {
                idx: 1,
                released: vec![],
            };
            let ret = pl_present_latest(
                (&mut c as *mut Codec).cast(),
                1,
                keep,
                &OutputOps { next, release },
            );
            assert_eq!((ret >> 32, ret as i32), (3, -2));
            assert_eq!(c.released, [(1, 0), (2, 0), (3, if keep { 1 } else { 2 })]);
        }
    }
}
