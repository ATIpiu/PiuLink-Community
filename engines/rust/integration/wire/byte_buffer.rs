use core::{mem, ptr};

#[repr(C)]
pub struct Buffer {
    data: *mut u8,
    length: u32,
    position: u32,
    order: u32,
}

#[no_mangle]
pub unsafe extern "C" fn BbInitializeWrappedBuffer(
    out: *mut Buffer,
    data: *mut u8,
    offset: i32,
    len: i32,
    order: i32,
) {
    let valid = !data.is_null() && offset >= 0 && len >= 0;
    ptr::write(
        out,
        Buffer {
            data: if valid {
                data.add(offset as usize)
            } else {
                ptr::null_mut()
            },
            length: if valid { len as u32 } else { 0 },
            position: 0,
            order: order as u32,
        },
    );
}

#[no_mangle]
pub extern "C" fn BbAdvanceBuffer(b: &mut Buffer, offset: i32) -> bool {
    if let Some(pos) = b.position.checked_add_signed(offset) {
        if pos <= b.length {
            b.position = pos;
            return true;
        }
    }
    false
}
impl Buffer {
    fn take(&mut self, n: u32) -> Option<*mut u8> {
        let pos = self.position;
        if self.data.is_null() || !BbAdvanceBuffer(self, n as i32) {
            return None;
        }
        Some(unsafe { self.data.add(pos as usize) })
    }
}

#[no_mangle]
pub extern "C" fn BbRewindBuffer(b: &mut Buffer) {
    b.position = 0;
}

#[no_mangle]
pub unsafe extern "C" fn BbGetBytes(b: &mut Buffer, out: *mut u8, len: i32) -> bool {
    if len == 0 { return true; }
    if len < 0 || out.is_null() { return false; }
    let Some(src) = b.take(len as u32) else {
        ptr::write_bytes(out, 0, len as usize);
        return false;
    };
    ptr::copy(src, out, len as usize);
    true
}

#[no_mangle]
pub unsafe extern "C" fn BbPutBytes(b: &mut Buffer, src: *const u8, len: i32) -> bool {
    if len == 0 { return true; }
    if len < 0 || src.is_null() { return false; }
    let Some(out) = b.take(len as u32) else { return false };
    ptr::copy(src, out, len as usize);
    true
}

macro_rules! access {
    ($get:ident,$put:ident,$t:ty) => {
        #[no_mangle]
        pub unsafe extern "C" fn $get(b: &mut Buffer, out: *mut $t) -> bool {
            if out.is_null() {
                return false;
            }
            let Some(p) = b.take(mem::size_of::<$t>() as u32) else {
                if cfg!(any(target_os = "ios", target_env = "ohos", all(target_os = "linux", target_env = "gnu"))) {
                    ptr::write_unaligned(out, 0);
                }
                return false;
            };
            let v = ptr::read_unaligned(p.cast::<$t>());
            ptr::write_unaligned(
                out,
                if b.order == 2 {
                    <$t>::from_be(v)
                } else {
                    <$t>::from_le(v)
                },
            );
            true
        }
        #[no_mangle]
        pub unsafe extern "C" fn $put(b: &mut Buffer, v: $t) -> bool {
            let Some(p) = b.take(mem::size_of::<$t>() as u32) else {
                return false;
            };
            ptr::write_unaligned(
                p.cast::<$t>(),
                if b.order == 2 { v.to_be() } else { v.to_le() },
            );
            true
        }
    };
}
access!(BbGet8, BbPut8, u8);
access!(BbGet16, BbPut16, u16);
access!(BbGet32, BbPut32, u32);
access!(BbGet64, BbPut64, u64);
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn bulk_reads_preserve_bounds_and_zero_on_underrun() {
        let mut data = [9u8; 6];
        let mut b = core::mem::MaybeUninit::<Buffer>::uninit();
        unsafe {
            BbInitializeWrappedBuffer(b.as_mut_ptr(), data.as_mut_ptr(), 1, 4, 1);
            let mut b = b.assume_init();
            let source = [1, 2, 3, 4];
            assert!(BbPutBytes(&mut b, source.as_ptr(), 4));
            assert!(!BbPutBytes(&mut b, source.as_ptr(), 1));
            assert!(BbGetBytes(&mut b, ptr::null_mut(), 0));
            let mut out = [7; 5];
            assert!(!BbGetBytes(&mut b, out.as_mut_ptr(), 5));
            assert_eq!(out, [0; 5]);
            assert_eq!(b.position, 4);
            BbRewindBuffer(&mut b);
            assert!(!BbGetBytes(&mut b, out.as_mut_ptr(), -1));
            assert!(BbGetBytes(&mut b, out.as_mut_ptr(), 4));
            assert_eq!(out, [1, 2, 3, 4, 0]);
            assert_eq!(data, [9, 1, 2, 3, 4, 9]);
        }
    }

    #[test]
    fn cursor_and_endian_are_stable_on_failure() {
        for order in [1, 2] {
            let mut data = [0xee; 18];
            let mut b = core::mem::MaybeUninit::<Buffer>::uninit();
            unsafe {
                BbInitializeWrappedBuffer(b.as_mut_ptr(), data.as_mut_ptr(), 1, 15, order);
                let mut b = b.assume_init();
                assert!(
                    BbPut8(&mut b, 0x11)
                        && BbPut16(&mut b, 0x1234)
                        && BbPut32(&mut b, 0x76543210)
                        && BbPut64(&mut b, 0x1020304050607080)
                );
                assert_eq!(
                    &data[2..4],
                    if order == 2 {
                        &[0x12, 0x34]
                    } else {
                        &[0x34, 0x12]
                    }
                );
                assert!(!BbPut8(&mut b, 0) && !BbAdvanceBuffer(&mut b, i32::MIN));
                assert_eq!(b.position, 15);
                let mut sentinel = 999;
                assert!(!BbGet32(&mut b, &mut sentinel));
                assert_eq!(sentinel, if cfg!(any(target_os = "ios", target_env = "ohos", all(target_os = "linux", target_env = "gnu"))) { 0 } else { 999 });
                assert!(BbAdvanceBuffer(&mut b, -15));
                let (mut a, mut c, mut d, mut e) = (0, 0, 0, 0);
                assert!(
                    BbGet8(&mut b, &mut a)
                        && BbGet16(&mut b, &mut c)
                        && BbGet32(&mut b, &mut d)
                        && BbGet64(&mut b, &mut e)
                );
                assert_eq!((a, c, d, e), (0x11, 0x1234, 0x76543210, 0x1020304050607080));
                assert_eq!(data[0], 0xee);
                assert_eq!(data[16], 0xee);
            }
        }
    }
}
