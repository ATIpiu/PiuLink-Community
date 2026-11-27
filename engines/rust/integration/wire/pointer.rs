fn header(out: &mut [u8], magic: u32) {
    let len = (out.len() - 4) as u32;
    out[..4].copy_from_slice(&len.to_be_bytes());
    out[4..8].copy_from_slice(&magic.to_le_bytes());
}
fn delta(remaining: &mut i32) -> i16 {
    let part = (*remaining).clamp(i16::MIN as i32, i16::MAX as i32);
    *remaining -= part;
    part as i16
}
#[no_mangle]
pub extern "C" fn pl_pointer_relative(
    out: &mut [u8; 12],
    x: &mut i32,
    y: &mut i32,
    modern: bool,
) -> bool {
    header(out, if modern { 7 } else { 6 });
    out[8..10].copy_from_slice(&delta(x).to_be_bytes());
    out[10..].copy_from_slice(&delta(y).to_be_bytes());
    *x != 0 || *y != 0
}
#[no_mangle]
pub extern "C" fn pl_pointer_absolute(out: &mut [u8; 18], x: i32, y: i32, w: i32, h: i32) {
    header(out, 5);
    for (bytes, v) in
        out[8..]
            .chunks_exact_mut(2)
            .zip([x, y, 0, w.wrapping_sub(1), h.wrapping_sub(1)])
    {
        bytes.copy_from_slice(&(v as i16).to_be_bytes());
    }
}
#[no_mangle]
pub extern "C" fn pl_pointer_button(out: &mut [u8; 9], action: u8, button: u8, modern: bool) {
    header(out, u32::from(action) + u32::from(modern));
    out[8] = button;
}
#[no_mangle]
pub extern "C" fn pl_pointer_scroll(out: &mut [u8; 14], amount: i16, modern: bool) {
    header(out, if modern { 10 } else { 9 });
    out[8..10].copy_from_slice(&amount.to_be_bytes());
    out[10..12].copy_from_slice(&amount.to_be_bytes());
    out[12..].fill(0);
}
#[no_mangle]
pub extern "C" fn pl_pointer_hscroll(out: &mut [u8; 10], amount: i16) {
    header(out, 0x55000001);
    out[8..].copy_from_slice(&amount.to_be_bytes());
}
#[repr(C)]
#[derive(Default)]
pub struct Pointer {
    x: i16,
    y: i16,
    w: i16,
    h: i16,
    button: u8,
    release: bool,
}
#[no_mangle]
pub unsafe extern "C" fn pl_pointer_read(data: *const u8, len: usize, out: &mut Pointer) -> bool {
    if data.is_null() || !matches!(len, 9 | 10 | 12 | 14 | 18) {
        return false;
    }
    let b = core::slice::from_raw_parts(data, len);
    let magic = u32::from_le_bytes([b[4], b[5], b[6], b[7]]);
    let word = |i: usize| i16::from_be_bytes([b[i], b[i + 1]]);
    let mut p = Pointer::default();
    match (len, magic) {
        (12, 7) => {
            p.x = word(8);
            p.y = word(10);
        }
        (18, 5) => {
            p.x = word(8);
            p.y = word(10);
            p.w = word(14);
            p.h = word(16);
        }
        (9, 8 | 9) => {
            p.button = b[8];
            p.release = magic == 9;
        }
        (14, 10) | (10, 0x55000001) => p.x = word(8),
        _ => return false,
    }
    *out = p;
    true
}
#[no_mangle]
pub extern "C" fn pl_scroll_delta(amount: i16, high_resolution: bool, remainder: &mut i32) -> i32 {
    if high_resolution {
        return i32::from(amount);
    }
    let sum = *remainder + i32::from(amount);
    *remainder = sum % 120;
    sum - *remainder
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn movement_survives_splitting_and_signed_wire_values() {
        for (mut x, mut y) in [(-65001, 170029), (0, 0), (32767, -32768), (32768, -32769)] {
            let expected = (x, y);
            let (mut sx, mut sy) = (0i32, 0i32);
            let mut bytes = [0; 12];
            loop {
                let more = pl_pointer_relative(&mut bytes, &mut x, &mut y, true);
                let mut p = Pointer::default();
                assert!(unsafe { pl_pointer_read(bytes.as_ptr(), bytes.len(), &mut p) });
                sx += i32::from(p.x);
                sy += i32::from(p.y);
                if !more {
                    break;
                }
            }
            assert_eq!((sx, sy), expected);
        }
        let mut a = [255; 18];
        pl_pointer_absolute(&mut a, 3399, 2199, 3400, 2200);
        assert_eq!(&a[..8], &[0, 0, 0, 14, 5, 0, 0, 0]);
        let mut p = Pointer::default();
        assert!(unsafe { pl_pointer_read(a.as_ptr(), 18, &mut p) });
        assert_eq!((p.x, p.y, p.w, p.h), (3399, 2199, 3399, 2199));
    }
    #[test]
    fn scroll_and_buttons_match_the_wire() {
        for action in [7, 8] {
            let mut b = [0; 9];
            pl_pointer_button(&mut b, action, 3, true);
            assert_eq!(b, [0, 0, 0, 5, action + 1, 0, 0, 0, 3]);
            let mut p = Pointer::default();
            assert!(unsafe { pl_pointer_read(b.as_ptr(), 9, &mut p) });
            assert_eq!((p.button, p.release), (3, action == 8));
        }
        for amount in [i16::MIN, -121, -1, 0, 1, 120, 32767] {
            let mut v = [0; 14];
            let mut h = [0; 10];
            pl_pointer_scroll(&mut v, amount, true);
            pl_pointer_hscroll(&mut h, amount);
            assert_eq!(&v[8..10], &amount.to_be_bytes());
            assert_eq!(&v[8..10], &v[10..12]);
            for bytes in [&v[..], &h[..]] {
                let mut p = Pointer::default();
                assert!(unsafe { pl_pointer_read(bytes.as_ptr(), bytes.len(), &mut p) });
                assert_eq!(p.x, amount);
            }
        }
        let mut rest = 0;
        assert_eq!(pl_scroll_delta(-90, false, &mut rest), 0);
        assert_eq!(pl_scroll_delta(-50, false, &mut rest), -120);
        assert_eq!(rest, -20);
        assert_eq!(pl_scroll_delta(8, true, &mut rest), 8);
        assert_eq!(pl_scroll_delta(139, false, &mut rest), 0);
        assert_eq!(pl_scroll_delta(1, false, &mut rest), 120);
        assert_eq!(rest, 0);
    }
}
