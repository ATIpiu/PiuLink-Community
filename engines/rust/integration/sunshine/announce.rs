use std::{ptr, slice};

macro_rules! attributes {
    ($( $field:ident , $key:literal, $default:expr; )*) => {
        #[repr(C)]
        #[derive(Clone, Copy, Debug)]
        pub struct Offer { $(pub $field: i64,)* }
        #[cfg(test)]
        fn required_lines() -> Vec<&'static [u8]> {
            let mut keys = Vec::new();
            $(if Option::<i64>::is_none(&$default) { keys.push($key.as_slice()); })*
            keys
        }

        fn parse(payload: &[u8]) -> Option<Offer> {
            $(let mut $field = None;)*
            for line in payload.split(|&b| b == b'\r' || b == b'\n') {
                let Some(attr) = line.strip_prefix(b"a=") else {continue};
                let Some(colon) = attr.iter().position(|&b| b == b':') else {continue};
                match &attr[..colon] {
                    $($key if $field.is_none() => {
                        let s = std::str::from_utf8(&attr[colon+1..]).ok()?.trim();
                        $field = Some(s.parse::<i64>().ok()?);
                    },)*
                    _ => (),
                }
            }
            let mut offer = Offer {$($field: $field.or($default)?,)*};
            if offer.fps <= 0 || offer.width <= 0 || offer.height <= 0
                || offer.packet_size <= 16 || offer.packet_size > 65507
                || offer.packet_duration <= 0 || offer.channels <= 0 || offer.channels > 8
                || offer.configured < 0 || offer.configured > i32::MAX.into() {
                return None;
            }
            $(let max = if matches!(stringify!($field), "encryption" | "client_flags" | "nv_flags") {
                u32::MAX as i64
            } else {
                i32::MAX as i64
            };
            if !(i32::MIN as i64..=max).contains(&offer.$field) {return None;})*
            if offer.refresh < 99 * offer.fps || offer.refresh > 101 * offer.fps {
                offer.refresh = 0;
            }
            Some(offer)
        }
    };
}
include!("announce_fields.rs");

#[no_mangle]
pub unsafe extern "C" fn pl_announce(payload: *const u8, len: i32, output: *mut Offer) -> bool {
    if payload.is_null() || len <= 0 || output.is_null() {
        return false;
    }
    let Some(value) = parse(slice::from_raw_parts(payload, len as usize)) else {
        return false;
    };
    ptr::write(output, value);
    true
}

#[no_mangle]
pub extern "C" fn pl_announced_bitrate(
    mut kbps: i64,
    fec: i32,
    quality: bool,
    channels: i32,
) -> i32 {
    if kbps <= 0 || kbps > i32::MAX as i64 || !(1..=8).contains(&channels) {
        return 0;
    }
    if (0..=80).contains(&fec) {
        kbps = (kbps as f32 / (100.0f32 / (100 - fec) as f32)) as i64;
    }
    kbps -= (i64::from(channels) * if quality { 256 } else { 96 }).min(kbps / 5);
    kbps -= 500.min(kbps / 10);
    kbps as i32
}

#[cfg(test)]
mod tests {
    use super::*;
    fn offer() -> String {
        let mut lines = String::new();
        for key in required_lines() {
            let k = std::str::from_utf8(key).unwrap();
            let value = if k.ends_with("packetSize") {
                1280
            } else if k.ends_with("maxFPS") {
                60
            } else {
                2
            };
            lines.push_str(&format!("a={k}:{value}\r\n"));
        }
        lines
    }
    #[test]
    fn negotiated_defaults_and_duplicate_precedence() {
        let p = offer();
        let s = parse(p.as_bytes()).unwrap();
        assert_eq!(
            (
                s.channels,
                s.fps,
                s.audio_qos,
                s.packet_duration,
                s.nv_flags
            ),
            (2, 60, 4, 5, 135)
        );
        let payload = format!("{p}a=x-nv-video[0].clientRefreshRateX100:5994 \na=x-nv-video[0].clientRefreshRateX100:12000");
        assert_eq!(parse(payload.as_bytes()).unwrap().refresh, 5994);
        assert_eq!(
            parse(format!("{p}a=x-nv-video[0].clientRefreshRateX100:12000").as_bytes())
                .unwrap()
                .refresh,
            0
        );
        for key in required_lines() {
            let key = std::str::from_utf8(key).unwrap();
            assert!(parse(p.replace(key, "ignored").as_bytes()).is_none());
        }
        for bad in ["", "-1", "2147483648", "cat"] {
            assert!(parse(p.replace("maxFPS:60", &format!("maxFPS:{bad}")).as_bytes()).is_none());
        }
    }
    #[test]
    fn video_budget_preserves_bandwidth_for_audio_and_repair() {
        assert_eq!(pl_announced_bitrate(45000, 20, true, 2), 34988);
        assert_eq!(pl_announced_bitrate(1000, 90, false, 8), 720);
        assert_eq!(pl_announced_bitrate(0, 20, false, 2), 0);
    }
}
