use std::ffi::c_void;

const PICTURE: i32 = 0;
const NEED_IDR: i32 = -1;

#[repr(C)]
pub struct Entry {
    next: *const Entry,
    data: *const u8,
    length: i32,
    kind: i32,
}

#[repr(C)]
pub struct DecoderSink {
    reserve: extern "C" fn(*mut c_void, i32) -> bool,
    write: extern "C" fn(*mut c_void, i32, *const u8, i32) -> bool,
    submit: extern "C" fn(*mut c_void, i32, i32) -> i32,
}

// The depacketizer retains the chain and payloads until this synchronous call returns.
#[no_mangle]
pub unsafe extern "C" fn pl_submit_decode_unit(
    head: *const Entry,
    length: i32,
    ctx: *mut c_void,
    sink: &DecoderSink,
) -> i32 {
    let mut cursor = head;
    let mut remaining = length;
    let mut picture_bytes = 0;
    while let Some(entry) = cursor.as_ref() {
        if entry.data.is_null()
            || entry.length <= 0
            || entry.length > remaining
            || !(0..=3).contains(&entry.kind)
        {
            return NEED_IDR;
        }
        remaining -= entry.length;
        if entry.kind == PICTURE {
            picture_bytes += entry.length;
        }
        cursor = entry.next;
    }
    if remaining != 0 || picture_bytes == 0 || !(sink.reserve)(ctx, length) {
        return NEED_IDR;
    }
    // Parameter sets and the picture share the Java scratch array. Finish sending
    // configuration before writing any picture bytes, even for interleaved entries.
    let mut offset = 0;
    for picture in [false, true] {
        cursor = head;
        while let Some(entry) = cursor.as_ref() {
            if (entry.kind == PICTURE) == picture {
                if !(sink.write)(ctx, offset, entry.data, entry.length) {
                    return NEED_IDR;
                }
                if picture {
                    offset += entry.length;
                } else {
                    let status = (sink.submit)(ctx, entry.length, entry.kind);
                    if status != 0 {
                        return status;
                    }
                }
            }
            cursor = entry.next;
        }
    }
    (sink.submit)(ctx, picture_bytes, PICTURE)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[derive(Default)]
    struct Sink {
        scratch: Vec<u8>,
        submitted: Vec<(i32, Vec<u8>)>,
        reject: i32,
    }
    extern "C" fn reserve(p: *mut c_void, n: i32) -> bool {
        unsafe { &mut *p.cast::<Sink>() }
            .scratch
            .resize(n as usize, 0);
        true
    }
    extern "C" fn write(p: *mut c_void, offset: i32, data: *const u8, n: i32) -> bool {
        let buf = &mut unsafe { &mut *p.cast::<Sink>() }.scratch;
        buf[offset as usize..(offset + n) as usize]
            .copy_from_slice(unsafe { std::slice::from_raw_parts(data, n as usize) });
        true
    }
    extern "C" fn submit(p: *mut c_void, n: i32, kind: i32) -> i32 {
        let sink = unsafe { &mut *p.cast::<Sink>() };
        sink.submitted
            .push((kind, sink.scratch[..n as usize].to_vec()));
        if kind == sink.reject {
            -1
        } else {
            0
        }
    }
    #[test]
    fn interleaved_configuration_and_picture_with_failure() {
        let ops = DecoderSink {
            reserve,
            write,
            submit,
        };
        let entry = |kind, data: &'static [u8], next| Entry {
            kind,
            data: data.as_ptr(),
            length: data.len() as i32,
            next,
        };
        let tail = entry(0, b"end", std::ptr::null());
        let middle = entry(1, b"SPS", &tail);
        let head = entry(0, b"start", &middle);
        let mut output = Sink {
            reject: -2,
            ..Default::default()
        };
        let p = (&mut output as *mut Sink).cast();
        for len in [0, 10, 12] {
            assert_eq!(unsafe { pl_submit_decode_unit(&head, len, p, &ops) }, -1);
        }
        assert!(output.submitted.is_empty());
        assert_eq!(unsafe { pl_submit_decode_unit(&head, 11, p, &ops) }, 0);
        assert_eq!(
            output.submitted,
            [(1, b"SPS".to_vec()), (0, b"startend".to_vec())]
        );
        output.reject = 1;
        assert_eq!(unsafe { pl_submit_decode_unit(&head, 11, p, &ops) }, -1);
        assert_eq!(output.submitted.len(), 3);
    }
}
