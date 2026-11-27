use std::ffi::c_void;
use std::ptr;

const BAD_ARG: i32 = -1;
const INTERNAL_ERROR: i32 = -3;
#[repr(C)]
pub struct Format {
    pub rate: i32,
    pub channels: i32,
    pub streams: i32,
    pub coupled: i32,
    pub samples: i32,
    pub mapping: [u8; 8],
}
impl Format {
    fn pcm_len(&self) -> Option<usize> {
        if !matches!(self.rate, 8000 | 12000 | 16000 | 24000 | 48000)
            || !(1..=8).contains(&self.channels)
            || !(1..=255).contains(&self.streams)
            || self.coupled < 0
            || self.coupled > self.streams
            || self.streams + self.coupled > 255
            || self.samples <= 0
            || self.samples > self.rate * 120 / 1000
        {
            return None;
        }
        let limit = self.streams + self.coupled;
        if self.mapping[..self.channels as usize]
            .iter()
            .any(|&c| c != 255 && i32::from(c) >= limit)
        {
            return None;
        }
        Some(self.samples as usize * self.channels as usize)
    }
}
#[repr(C)]
pub struct EncoderOps {
    create: extern "C" fn(&Format, i32, &mut i32) -> *mut c_void,
    configure: extern "C" fn(*mut c_void, i32, i32) -> i32,
    destroy: extern "C" fn(*mut c_void),
    read: extern "C" fn(*mut c_void, &mut *const f32, &mut usize) -> i32,
    encode: extern "C" fn(*mut c_void, *const f32, i32, *mut u8, i32) -> i32,
    send: extern "C" fn(*mut c_void, *const u8, i32) -> i32,
}
struct Codec {
    handle: *mut c_void,
    release: extern "C" fn(*mut c_void),
}
impl Drop for Codec {
    fn drop(&mut self) {
        (self.release)(self.handle)
    }
}
#[no_mangle]
pub extern "C" fn pl_opus_encode_stream(
    fmt: &Format,
    bitrate: i32,
    ops: &EncoderOps,
    ctx: *mut c_void,
) -> i32 {
    let Some(needed) = fmt.pcm_len() else {
        return BAD_ARG;
    };
    if bitrate <= 0 {
        return BAD_ARG;
    }
    let mut err = 0;
    // OPUS_APPLICATION_RESTRICTED_LOWDELAY; bitrate and CBR are set before any frame.
    let handle = (ops.create)(fmt, 2051, &mut err);
    if handle.is_null() {
        return if err != 0 { err } else { INTERNAL_ERROR };
    }
    let codec = Codec {
        handle,
        release: ops.destroy,
    };
    for (request, value) in [(4002, bitrate), (4006, 0)] {
        let status = (ops.configure)(codec.handle, request, value);
        if status != 0 {
            return status;
        }
    }
    let mut packet = [0u8; 1400];
    loop {
        let mut data = ptr::null();
        let mut count = 0;
        match (ops.read)(ctx, &mut data, &mut count) {
            1 => {}
            other => return other,
        }
        if data.is_null() || count < needed {
            return BAD_ARG;
        }
        let n = (ops.encode)(
            codec.handle,
            data,
            fmt.samples,
            packet.as_mut_ptr(),
            packet.len() as i32,
        );
        if n < 0 {
            return n;
        }
        if n as usize > packet.len() {
            return INTERNAL_ERROR;
        }
        let status = (ops.send)(ctx, packet.as_ptr(), n);
        if status != 0 {
            return status;
        }
    }
}

#[repr(C)]
pub struct DecoderOps {
    create: extern "C" fn(&Format, &mut i32) -> *mut c_void,
    decode: extern "C" fn(*mut c_void, *const u8, i32, *mut i16, i32, i32) -> i32,
    destroy: extern "C" fn(*mut c_void),
}
pub struct Decoder {
    codec: Codec,
    decode: extern "C" fn(*mut c_void, *const u8, i32, *mut i16, i32, i32) -> i32,
    channels: i32,
    samples: i32,
    pcm: Vec<i16>,
}
#[no_mangle]
pub extern "C" fn pl_opus_decoder_new(
    fmt: &Format,
    ops: &DecoderOps,
    err: &mut i32,
) -> *mut Decoder {
    *err = BAD_ARG;
    let Some(count) = fmt.pcm_len() else {
        return ptr::null_mut();
    };
    let handle = (ops.create)(fmt, err);
    if handle.is_null() {
        return ptr::null_mut();
    }
    let codec = Codec {
        handle,
        release: ops.destroy,
    };
    let mut pcm = Vec::new();
    if pcm.try_reserve_exact(count).is_err() {
        *err = -7;
        return ptr::null_mut();
    }
    pcm.resize(count, 0);
    *err = 0;
    Box::into_raw(Box::new(Decoder {
        codec,
        decode: ops.decode,
        channels: fmt.channels,
        samples: fmt.samples,
        pcm,
    }))
}
#[no_mangle]
pub extern "C" fn pl_opus_decode(
    dec: &mut Decoder,
    packet: *const u8,
    len: i32,
    out: &mut *const i16,
) -> i32 {
    *out = ptr::null();
    if len < 0 || (len != 0 && packet.is_null()) {
        return BAD_ARG;
    }
    // A missing packet requests Opus PLC for one negotiated audio frame.
    let n = (dec.decode)(
        dec.codec.handle,
        if len == 0 { ptr::null() } else { packet },
        len,
        dec.pcm.as_mut_ptr(),
        dec.samples,
        0,
    );
    if n <= 0 {
        return n;
    }
    if n > dec.samples {
        return INTERNAL_ERROR;
    }
    *out = dec.pcm.as_ptr();
    n * dec.channels
}
#[no_mangle]
pub unsafe extern "C" fn pl_opus_decoder_free(dec: *mut Decoder) {
    if !dec.is_null() {
        drop(Box::from_raw(dec))
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pcm_shape_has_interleaved_channels_and_valid_topology() {
        let mut fmt = Format {
            rate: 48000,
            channels: 6,
            streams: 4,
            coupled: 2,
            samples: 240,
            mapping: [0, 4, 1, 5, 2, 3, 0, 0],
        };
        assert_eq!(fmt.pcm_len(), Some(1440));
        fmt.mapping[1] = 6;
        assert_eq!(fmt.pcm_len(), None);
        fmt.mapping[1] = 255;
        fmt.samples = 5760;
        assert_eq!(fmt.pcm_len(), Some(34560));
        fmt.samples += 1;
        assert_eq!(fmt.pcm_len(), None);
    }
}
