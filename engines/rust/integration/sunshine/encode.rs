use std::ffi::c_void;

type DriverCall = unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32;
#[repr(C)]
pub struct EncoderOps {
    map: DriverCall,
    submit: DriverCall,
    lock: DriverCall,
    unlock: DriverCall,
    unmap: DriverCall,
    prepare: extern "C" fn(*mut c_void),
    wait: extern "C" fn(*mut c_void, u32) -> bool,
    copy: extern "C" fn(*mut c_void) -> bool,
    report: extern "C" fn(*mut c_void, u8, i32),
}
#[repr(C)]
pub struct Frame {
    encoder: *mut c_void,
    ctx: *mut c_void,
    map: *mut c_void,
    picture: *mut c_void,
    lock: *mut c_void,
    resource: *const *mut c_void,
    output: *mut c_void,
    asynchronous: bool,
}
struct Lease<'a> {
    frame: &'a Frame,
    release: DriverCall,
    handle: *mut c_void,
    report: extern "C" fn(*mut c_void, u8, i32),
    stage: u8,
}
impl Drop for Lease<'_> {
    fn drop(&mut self) {
        let err = unsafe { (self.release)(self.frame.encoder, self.handle) };
        if err != 0 {
            (self.report)(self.frame.ctx, self.stage, err)
        }
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_encode_frame(f: &Frame, ops: &EncoderOps) -> bool {
    if f.encoder.is_null()
        || f.resource.is_null()
        || f.map.is_null()
        || f.picture.is_null()
        || f.lock.is_null()
    {
        return false;
    }
    let call = |driver: DriverCall, params, stage| {
        let status = driver(f.encoder, params);
        if status != 0 {
            (ops.report)(f.ctx, stage, status)
        }
        status == 0
    };
    if !call(ops.map, f.map, 0) {
        return false;
    }
    let _input = Lease {
        frame: f,
        release: ops.unmap,
        handle: *f.resource,
        report: ops.report,
        stage: 6,
    };
    (ops.prepare)(f.ctx);
    if !call(ops.submit, f.picture, 1) {
        return false;
    }
    if f.asynchronous && !(ops.wait)(f.ctx, 100) {
        (ops.report)(f.ctx, 2, 0);
        return false;
    }
    if !call(ops.lock, f.lock, 3) {
        return false;
    }
    let _bits = Lease {
        frame: f,
        release: ops.unlock,
        handle: f.output,
        report: ops.report,
        stage: 5,
    };
    let copied = (ops.copy)(f.ctx);
    if !copied {
        (ops.report)(f.ctx, 4, 0);
    }
    copied
}
#[no_mangle]
pub unsafe extern "C" fn pl_nv_invalidate(
    encoder: *mut c_void,
    call: unsafe extern "system" fn(*mut c_void, u64) -> i32,
    first: u64,
    last: u64,
    encoded: u64,
    dpb: u32,
    prev_start: &mut u64,
    prev_end: &mut u64,
    pending: &mut bool,
) -> bool {
    if first > last {
        return false;
    }
    if first >= *prev_start && last <= *prev_end {
        return true;
    }
    let Some(span) = encoded.checked_sub(first) else {
        return false;
    };
    if dpb <= 1 || span >= u64::from(dpb - 1) {
        return false;
    }
    for i in first..=encoded {
        if call(encoder, i) != 0 {
            return false;
        }
    }
    *prev_start = first;
    *prev_end = encoded;
    *pending = true;
    true
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    #[derive(Default)]
    struct Fake {
        fail: u8,
        events: Vec<u8>,
    }
    thread_local! {static FAKE:RefCell<Fake>=RefCell::new(Fake::default());}
    fn step(stage: u8) -> bool {
        FAKE.with(|f| {
            let mut f = f.borrow_mut();
            f.events.push(stage);
            f.fail != stage
        })
    }
    macro_rules! drv {
        ($name:ident,$i:expr) => {
            unsafe extern "system" fn $name(_: *mut c_void, _: *mut c_void) -> i32 {
                if step($i) {
                    0
                } else {
                    1
                }
            }
        };
    }
    drv!(map, 0);
    drv!(submit, 1);
    drv!(lock, 3);
    drv!(unlock, 5);
    drv!(unmap, 6);
    extern "C" fn prepare(_: *mut c_void) {}
    extern "C" fn wait(_: *mut c_void, ms: u32) -> bool {
        assert_eq!(ms, 100);
        step(2)
    }
    extern "C" fn copy(_: *mut c_void) -> bool {
        step(4)
    }
    extern "C" fn report(_: *mut c_void, _: u8, _: i32) {}
    #[test]
    fn driver_leases_release_on_each_failure() {
        let ops = EncoderOps {
            map,
            submit,
            lock,
            unlock,
            unmap,
            prepare,
            wait,
            copy,
            report,
        };
        let p = std::ptr::dangling_mut::<u8>().cast();
        let f = Frame {
            encoder: p,
            ctx: p,
            map: p,
            picture: p,
            lock: p,
            resource: &p,
            output: p,
            asynchronous: true,
        };
        for (failed, expected) in [
            (0, vec![0]),
            (1, vec![0, 1, 6]),
            (2, vec![0, 1, 2, 6]),
            (3, vec![0, 1, 2, 3, 6]),
            (4, vec![0, 1, 2, 3, 4, 5, 6]),
            (99, vec![0, 1, 2, 3, 4, 5, 6]),
        ] {
            FAKE.with(|f| {
                *f.borrow_mut() = Fake {
                    fail: failed,
                    events: vec![],
                }
            });
            assert_eq!(unsafe { pl_encode_frame(&f, &ops) }, failed == 99);
            FAKE.with(|f| assert_eq!(f.borrow().events, expected));
        }
    }
    #[test]
    fn failed_invalidation_can_be_retried() {
        unsafe extern "system" fn invalidate(_: *mut c_void, i: u64) -> i32 {
            if step(i as u8) {
                0
            } else {
                1
            }
        }
        let (mut a, mut b, mut pending) = (0, 0, false);
        let p = std::ptr::null_mut();
        FAKE.with(|f| {
            *f.borrow_mut() = Fake {
                fail: 6,
                events: vec![],
            }
        });
        assert!(!unsafe {
            pl_nv_invalidate(p, invalidate, 5, 6, 7, 6, &mut a, &mut b, &mut pending)
        });
        assert_eq!((a, b, pending), (0, 0, false));
        FAKE.with(|f| f.borrow_mut().fail = 99);
        assert!(unsafe {
            pl_nv_invalidate(p, invalidate, 5, 6, 7, 6, &mut a, &mut b, &mut pending)
        });
        assert_eq!((a, b, pending), (5, 7, true));
    }
}
