use core::slice;

fn prefix(bytes: &[u8]) -> usize {
    match bytes {
        [0, 0, 0, 1, _, ..] => 4,
        [0, 0, 1, _, ..] => 3,
        _ => 0,
    }
}

fn next(bytes: &[u8]) -> usize {
    let start = prefix(bytes);
    let mut pos = start;
    while pos + 3 < bytes.len() {
        if prefix(&bytes[pos..]) != 0 {
            return pos;
        }
        pos += 1;
    }
    bytes.len()
}

#[repr(u8)]
#[derive(Copy, Clone, PartialEq)]
enum Kind {
    Other,
    Sps,
    Pps,
    Vps,
    Aud,
    Sei,
    Reference,
}
fn kind(bytes: &[u8], hevc: bool) -> Kind {
    let start = prefix(bytes);
    if start == 0 {
        return Kind::Other;
    }
    if hevc {
        match (bytes[start] >> 1) & 63 {
            16..=21 => Kind::Reference,
            32 => Kind::Vps,
            33 => Kind::Sps,
            34 => Kind::Pps,
            35 => Kind::Aud,
            39 => Kind::Sei,
            _ => Kind::Other,
        }
    } else {
        match bytes[start] & 31 {
            5 => Kind::Reference,
            6 => Kind::Sei,
            7 => Kind::Sps,
            8 => Kind::Pps,
            9 => Kind::Aud,
            _ => Kind::Other,
        }
    }
}

// FFI callers supply the exact live packet range; zero-length ranges may be null.
unsafe fn payload<'a>(ptr: *const u8, len: u32) -> &'a [u8] {
    if len == 0 || ptr.is_null() {
        &[]
    } else {
        slice::from_raw_parts(ptr, len as usize)
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_nal_kind(ptr: *const u8, len: u32, hevc: bool) -> u8 {
    kind(payload(ptr, len), hevc) as u8
}

#[no_mangle]
pub unsafe extern "C" fn pl_nal_next(ptr: *const u8, len: u32) -> u32 {
    next(payload(ptr, len)) as u32
}

// Strip leading padding, one optional AUD and all prefix SEIs, as Moonlight does.
#[no_mangle]
pub unsafe extern "C" fn pl_nal_leading(ptr: *const u8, len: u32, hevc: bool) -> u32 {
    let bytes = payload(ptr, len);
    let mut at = if prefix(bytes) == 0 { next(bytes) } else { 0 };
    if kind(&bytes[at..], hevc) == Kind::Aud {
        at += next(&bytes[at..]);
    }
    while kind(&bytes[at..], hevc) == Kind::Sei {
        at += next(&bytes[at..]);
    }
    at as u32
}

// Return a fragment of an IDR packet. Config NALs remain separate; all bytes
// from the first reference slice through the packet end share its allocation.
#[repr(C)]
pub struct Fragment {
    offset: u32,
    end: u32,
    reference: bool,
}
#[no_mangle]
pub unsafe extern "C" fn pl_nal_fragment(
    ptr: *const u8,
    len: u32,
    hevc: bool,
    out: &mut Fragment,
) -> bool {
    let bytes = payload(ptr, len);
    let mut at = 0;
    while at < bytes.len() {
        let remaining = &bytes[at..];
        let k = kind(remaining, hevc);
        if prefix(remaining) == 0 || matches!(k, Kind::Aud | Kind::Sei) {
            at += next(remaining);
            continue;
        }
        let reference = k == Kind::Reference;
        let end = if reference {
            bytes.len()
        } else {
            at + next(remaining)
        };
        *out = Fragment {
            offset: at as u32,
            end: end as u32,
            reference,
        };
        return true;
    }
    false
}
