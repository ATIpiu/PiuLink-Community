use std::{
    ffi::{c_char, c_void, CStr},
    ptr, slice,
};

const OPTIONS: u8 = 0;
const DESCRIBE: u8 = 1;
const AUDIO: u8 = 2;
const VIDEO: u8 = 3;
const CONTROL: u8 = 4;
const ANNOUNCE: u8 = 5;
const PLAY: u8 = 6;
fn handshake(major: i32, unified: bool, mut exchange: impl FnMut(u8, &'static [u8]) -> i32) -> i32 {
    let modern = major >= 5;
    let setup_audio: &[u8] = if modern {
        b"streamid=audio/0/0\0"
    } else {
        b"streamid=audio\0"
    };
    let setup_video: &[u8] = if modern {
        b"streamid=video/0/0\0"
    } else {
        b"streamid=video\0"
    };
    let control: &[u8] = if unified {
        b"streamid=control/13/0\0"
    } else {
        b"streamid=control/1/0\0"
    };
    for (op, target, enabled) in [
        (OPTIONS, &b"\0"[..], true),
        (DESCRIBE, b"\0", true),
        (AUDIO, setup_audio, true),
        (VIDEO, setup_video, true),
        (CONTROL, control, modern),
        (ANNOUNCE, b"\0", true),
        (PLAY, b"/\0", unified),
        (PLAY, b"streamid=video\0", !unified),
        (PLAY, b"streamid=audio\0", !unified),
    ] {
        if enabled {
            let code = exchange(op, target);
            if code != 0 {
                return code;
            }
        }
    }
    0
}
#[no_mangle]
pub extern "C" fn pl_rtsp_handshake(
    ctx: *mut c_void,
    major: i32,
    unified: bool,
    cb: extern "C" fn(*mut c_void, u8, *const c_char) -> i32,
) -> i32 {
    handshake(major, unified, |op, target| {
        cb(ctx, op, target.as_ptr().cast())
    })
}
#[no_mangle]
pub unsafe extern "C" fn pl_setup_kind(target: *const c_char) -> u8 {
    if target.is_null() {
        return 0;
    }
    let s = CStr::from_ptr(target).to_bytes();
    let Some(i) = s.iter().position(|b| *b == b'=') else {
        return 0;
    };
    match s[i + 1..].split(|b| *b == b'/').next() {
        Some(b"audio") => AUDIO,
        Some(b"video") => VIDEO,
        Some(b"control") => CONTROL,
        _ => 0,
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_transport_port(value: *const c_char) -> u16 {
    if value.is_null() {
        return 0;
    }
    let value = CStr::from_ptr(value).to_bytes();
    for field in value.split(|b| *b == b';') {
        let field = field.trim_ascii();
        if let Some(s) = field.strip_prefix(b"server_port=") {
            let port = s
                .split(|b| *b == b'-')
                .next()
                .unwrap_or_default()
                .trim_ascii();
            if !port.iter().all(u8::is_ascii_digit) {
                return 0;
            }
            return std::str::from_utf8(port)
                .ok()
                .and_then(|s| s.parse().ok())
                .unwrap_or(0);
        }
    }
    0
}
fn contains(data: &[u8], term: &[u8]) -> bool {
    data.windows(term.len()).any(|w| w == term)
}
fn video_format(sdp: &[u8], client: u32, host: u32) -> i32 {
    if client & 0xf000 != 0 && contains(sdp, b"AV1/90000") {
        if client & 0x2000 != 0 && host & 0x20000 != 0 {
            0x2000
        } else {
            0x1000
        }
    } else if client & 0xf00 != 0 && contains(sdp, b"sprop-parameter-sets=AAAAAU") {
        if client & 0x200 != 0 && host & 0x200 != 0 {
            0x200
        } else {
            0x100
        }
    } else {
        1
    }
}
#[repr(C)]
#[derive(Debug, Default)]
pub struct Opus {
    rate: i32,
    channels: i32,
    streams: i32,
    coupled: i32,
    samples: i32,
    map: [u8; 8],
}
fn opus(sdp: &[u8], count: usize) -> Result<(Opus, Opus, bool), i32> {
    if !(1..=8).contains(&count) {
        return Err(-4);
    }
    let empty = || Opus {
        rate: 48000,
        ..Opus::default()
    };
    let mut normal = empty();
    normal.channels = count as i32;
    if count == 2 {
        normal.streams = 1;
        normal.coupled = 1;
        normal.map[1] = 1;
        return Ok((normal, empty(), false));
    }
    let prefix = format!("a=fmtp:97 surround-params={}", count);
    let mut matches = sdp.windows(prefix.len()).enumerate().filter_map(|(i, w)| {
        if w == prefix.as_bytes() {
            Some(&sdp[i + w.len()..])
        } else {
            None
        }
    });
    let read = |params: &[u8]| -> Result<Opus, i32> {
        let digit = |i: usize, err: i32| {
            params
                .get(i)
                .filter(|v| v.is_ascii_digit())
                .map(|v| v - b'0')
                .ok_or(err)
        };
        let mut op = empty();
        op.channels = count as i32;
        op.streams = i32::from(digit(0, -1)?);
        op.coupled = i32::from(digit(1, -2)?);
        for i in 0..count {
            op.map[i] = digit(i + 2, -3)?;
        }
        Ok(op)
    };
    if let Some(params) = matches.next() {
        normal = read(params)?;
        if matches!(count, 6 | 8) {
            normal.map[3..count].rotate_right(1);
        }
        if let Some(next) = matches.next() {
            return Ok((normal, read(next)?, true));
        }
    } else if count == 6 {
        normal.streams = 4;
        normal.coupled = 2;
        normal.map[..6].copy_from_slice(&[0, 4, 1, 5, 2, 3]);
    } else {
        return Err(-4);
    }
    Ok((normal, empty(), false))
}
#[no_mangle]
pub unsafe extern "C" fn pl_rtsp_describe(
    data: *const u8,
    len: i32,
    client: u32,
    host: u32,
    channels: i32,
    fmt: &mut i32,
    normal: *mut Opus,
    high: *mut Opus,
    hq: &mut bool,
    rfi: &mut bool,
) -> i32 {
    if data.is_null() || len <= 0 || normal.is_null() || high.is_null() {
        return -1;
    }
    let data = slice::from_raw_parts(data, len as usize);
    match opus(data, channels as usize) {
        Ok((n, h, quality)) => {
            *fmt = video_format(data, client, host);
            ptr::write(normal, n);
            ptr::write(high, h);
            *hq = quality;
            *rfi = contains(data, b"x-nv-video[0].refPicInvalidation");
            0
        }
        Err(code) => code,
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn session_generations_and_first_error() {
        for (major, unified, seq) in [
            (3, false, vec![0, 1, 2, 3, 5, 6, 6]),
            (5, false, vec![0, 1, 2, 3, 4, 5, 6, 6]),
            (7, true, vec![0, 1, 2, 3, 4, 5, 6]),
        ] {
            for fail in 0..=seq.len() {
                let mut ops = vec![];
                let result = handshake(major, unified, |op, target| {
                    assert_eq!(target.last(), Some(&0));
                    ops.push(op);
                    if ops.len() == fail + 1 {
                        454
                    } else {
                        0
                    }
                });
                assert_eq!(result, if fail == seq.len() { 0 } else { 454 });
                assert_eq!(ops, &seq[..ops.len()]);
            }
        }
    }
    #[test]
    fn codec_and_opus() {
        let sdp=b"AV1/90000 sprop-parameter-sets=AAAAAU\r\na=fmtp:97 surround-params=642012345\r\na=fmtp:97 surround-params=660012345";
        assert_eq!(video_format(sdp, 0x3301, 0x20200), 0x2000);
        assert_eq!(video_format(sdp, 0x301, 0), 0x100);
        assert_eq!(video_format(b"", 0x3301, 0x20200), 1);
        let (n, h, hq) = opus(sdp, 6).unwrap();
        assert!(hq);
        assert_eq!(&n.map[..6], &[0, 1, 2, 5, 3, 4]);
        assert_eq!(&h.map[..6], &[0, 1, 2, 3, 4, 5]);
        assert_eq!(opus(b"a=fmtp:97 surround-params=84212", 8).unwrap_err(), -3);
        assert_eq!(opus(b"", 8).unwrap_err(), -4);
        assert_eq!(
            unsafe {
                pl_transport_port(
                    b"unicast;server_port=48000-48001;source=X\0"
                        .as_ptr()
                        .cast(),
                )
            },
            48000
        );
        assert_eq!(
            unsafe { pl_setup_kind(b"streamid=control/13/0\0".as_ptr().cast()) },
            CONTROL
        );
    }
}
