use std::ffi::c_void;

#[repr(C)]
pub struct AvOps {
    send: unsafe extern "C" fn(*mut c_void, *const c_void) -> i32,
    receive: unsafe extern "C" fn(*mut c_void, *mut c_void) -> i32,
    alloc: unsafe extern "C" fn() -> *mut c_void,
    free: unsafe extern "C" fn(*mut *mut c_void),
    again: i32,
    eof: i32,
}

/// The sink takes ownership on every invocation, including a failed publication.
type Publish = extern "C" fn(*mut c_void, *mut c_void) -> bool;
unsafe fn drain(
    codec: *mut c_void,
    ctx: *mut c_void,
    ops: &AvOps,
    publish: Option<Publish>,
) -> Result<usize, i32> {
    let mut delivered = 0;
    loop {
        let mut packet = (ops.alloc)();
        if packet.is_null() {
            return Err(-12);
        }
        let result = (ops.receive)(codec, packet);
        if result < 0 {
            (ops.free)(&mut packet);
            return if result == ops.again || result == ops.eof {
                Ok(delivered)
            } else {
                Err(result)
            };
        }
        if let Some(sink) = publish {
            if !sink(ctx, packet) {
                return Err(-1);
            }
        } else {
            (ops.free)(&mut packet);
        }
        delivered += 1;
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_avcodec_encode(
    codec: *mut c_void,
    frame: *const c_void,
    ctx: *mut c_void,
    ops: &AvOps,
    publish: Option<Publish>,
) -> i32 {
    if codec.is_null() {
        return -1;
    }
    loop {
        let result = (ops.send)(codec, frame);
        if result == ops.eof && frame.is_null() {
            return 0;
        }
        if result != ops.again && result < 0 {
            return result;
        }
        match drain(codec, ctx, ops, publish) {
            Err(e) => return e,
            Ok(_) if result != ops.again => return 0,
            Ok(0) => return ops.again,
            _ => (),
        }
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::{cell::RefCell, ptr};
    struct State {
        submitted: usize,
        ready: usize,
        allocated: usize,
        freed: usize,
        shown: usize,
    }
    thread_local! {static S:RefCell<State>=const{RefCell::new(State{submitted:0,ready:1,allocated:0,freed:0,shown:0})};}
    unsafe extern "C" fn send(_: *mut c_void, _: *const c_void) -> i32 {
        S.with(|s| {
            let mut s = s.borrow_mut();
            s.submitted += 1;
            if s.ready != 0 {
                -11
            } else {
                s.ready = 2;
                0
            }
        })
    }
    unsafe extern "C" fn receive(_: *mut c_void, _: *mut c_void) -> i32 {
        S.with(|s| {
            let mut s = s.borrow_mut();
            if s.ready == 0 {
                -11
            } else {
                s.ready -= 1;
                0
            }
        })
    }
    unsafe extern "C" fn alloc() -> *mut c_void {
        S.with(|s| s.borrow_mut().allocated += 1);
        std::ptr::dangling_mut::<u8>().cast()
    }
    unsafe extern "C" fn free(_: *mut *mut c_void) {
        S.with(|s| s.borrow_mut().freed += 1);
    }
    extern "C" fn publish(_: *mut c_void, _: *mut c_void) -> bool {
        S.with(|s| s.borrow_mut().shown += 1);
        true
    }
    #[test]
    fn drains_pending_output_before_retry_and_releases_flush_packets() {
        let ops = AvOps {
            send,
            receive,
            alloc,
            free,
            again: -11,
            eof: -100,
        };
        let codec = std::ptr::dangling_mut::<u8>().cast();
        assert_eq!(
            unsafe { pl_avcodec_encode(codec, codec, ptr::null_mut(), &ops, Some(publish)) },
            0
        );
        S.with(|s| {
            let s = s.borrow();
            assert_eq!((s.submitted, s.shown, s.freed, s.allocated), (2, 3, 2, 5));
        });
        assert_eq!(
            unsafe { pl_avcodec_encode(codec, ptr::null(), ptr::null_mut(), &ops, None) },
            0
        );
        S.with(|s| {
            let s = s.borrow();
            assert_eq!(s.allocated, s.freed + s.shown);
        });
    }
}
