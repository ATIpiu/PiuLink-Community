use std::ffi::{c_char, c_void, CStr};
use std::{ptr, slice};

#[repr(C)]
pub struct OptionItem {
    flags: u8,
    name: *mut c_char,
    value: *mut c_char,
    next: *mut OptionItem,
}
#[repr(C)]
#[derive(Copy, Clone)]
struct Request {
    command: *mut c_char,
    target: *mut c_char,
}
#[repr(C)]
#[derive(Copy, Clone)]
struct Response {
    reason: *mut c_char,
    status: i32,
}
#[repr(C)]
union StartLine {
    request: Request,
    response: Response,
}
#[repr(C)]
pub struct Message {
    kind: u8,
    flags: u8,
    seq: i32,
    protocol: *mut c_char,
    options: *mut OptionItem,
    payload: *mut u8,
    payload_len: i32,
    buffer: *mut u8,
    line: StartLine,
}
type Allocate = unsafe extern "C" fn(usize) -> *mut c_void;
type Free = unsafe extern "C" fn(*mut c_void);

fn number(bytes: &[u8]) -> i32 {
    let mut at = 0;
    while at < bytes.len() && bytes[at].is_ascii_whitespace() {
        at += 1;
    }
    let neg = bytes.get(at) == Some(&b'-');
    if neg || bytes.get(at) == Some(&b'+') {
        at += 1;
    }
    let mut value = 0i32;
    for &b in &bytes[at..] {
        if !b.is_ascii_digit() {
            break;
        }
        value = value.saturating_mul(10).saturating_add(if neg {
            -(i32::from(b - b'0'))
        } else {
            i32::from(b - b'0')
        });
    }
    value
}

// Spans index the immutable input. The separately owned message buffer receives
// NUL terminators only after a header has been validated.
#[derive(Clone, Copy)]
struct Span {
    start: usize,
    end: usize,
}
impl Span {
    fn bytes(self, b: &[u8]) -> &[u8] {
        &b[self.start..self.end]
    }
    unsafe fn terminate(self, buf: *mut u8) -> *mut c_char {
        *buf.add(self.end) = 0;
        buf.add(self.start).cast()
    }
}

fn word(b: &[u8], at: &mut usize, limit: usize) -> Option<Span> {
    while *at < limit && b[*at] == b' ' {
        *at += 1;
    }
    let start = *at;
    while *at < limit && b[*at] != b' ' {
        *at += 1;
    }
    (*at > start).then_some(Span { start, end: *at })
}

struct Parsed {
    response: bool,
    first: Span,
    second: Span,
    third: Span,
    body: usize,
    options: Vec<(Span, Span)>,
}
fn parse(b: &[u8]) -> Result<Parsed, i32> {
    let headers = &b[..b.len().min(16 * 1024)];
    let newline = |at: usize| {
        headers
            .get(at..)?
            .windows(2)
            .position(|s| s == b"\r\n")
            .map(|p| p + at)
    };
    let end = newline(0).ok_or(-2)?;
    if b[..end].contains(&0) {
        return Err(-2);
    }
    let mut at = 0;
    let first = word(b, &mut at, end).ok_or(-2)?;
    let second = word(b, &mut at, end).ok_or(-2)?;
    let response = first.bytes(b).starts_with(b"RTSP");
    let third = if response {
        if at >= end || at + 1 >= end {
            return Err(-2);
        }
        Span { start: at + 1, end }
    } else {
        word(b, &mut at, end).ok_or(-2)?
    };
    if (if response { first } else { third }).bytes(b) != b"RTSP/1.0" {
        return Err(-2);
    }
    let mut options: Vec<(Span, Span)> = Vec::new();
    let mut lines = 0;
    at = end + 2;
    loop {
        if headers.get(at..).is_some_and(|tail| tail.starts_with(b"\r\n")) {
            at += 2;
            break;
        }
        // Historical ENet hosts omit one or both bytes of the final CRLF.
        if at == b.len() || b[at..] == *b"\r" {
            if options.is_empty() {
                return Err(-2);
            }
            at = b.len();
            break;
        }
        lines += 1;
        if lines > 128 {
            return Err(-2);
        }
        let end = newline(at).ok_or(-2)?;
        let line = &b[at..end];
        if line.contains(&0) {
            return Err(-2);
        }
        let colon = line.iter().position(|&v| v == b':').ok_or(-2)?;
        if colon == 0 {
            return Err(-2);
        }
        let name = Span {
            start: at,
            end: at + colon,
        };
        let mut val = at + colon + 1;
        if b.get(val) == Some(&b' ') {
            val += 1;
        }
        let value = Span { start: val, end };
        if let Some(item) = options
            .iter_mut()
            .find(|(key, _)| key.bytes(b) == name.bytes(b))
        {
            item.1 = value;
        } else {
            options.try_reserve(1).map_err(|_| -1)?;
            options.push((name, value));
        }
        at = end + 2;
    }
    Ok(Parsed {
        response,
        first,
        second,
        third,
        body: at,
        options,
    })
}

#[no_mangle]
pub unsafe extern "C" fn pl_rtsp_parse(
    out: *mut Message,
    input: *const u8,
    len: i32,
    alloc: Allocate,
    free: Free,
) -> i32 {
    if input.is_null() || out.is_null() || len <= 0 {
        return -2;
    }
    let b = slice::from_raw_parts(input, len as usize);
    let parsed = match parse(b) {
        Ok(v) => v,
        Err(err) => return err,
    };
    let buf = alloc(b.len() + 1).cast::<u8>();
    if buf.is_null() {
        return -1;
    }
    ptr::copy_nonoverlapping(input, buf, b.len());
    *buf.add(b.len()) = 0;
    let mut options: *mut OptionItem = ptr::null_mut();
    let mut tail = &mut options as *mut *mut OptionItem;
    let mut seq = -1;
    for (k, v) in parsed.options {
        let node = alloc(std::mem::size_of::<OptionItem>()).cast::<OptionItem>();
        if node.is_null() {
            while !options.is_null() {
                let next = (*options).next;
                free(options.cast());
                options = next;
            }
            free(buf.cast());
            return -1;
        }
        if k.bytes(b) == b"CSeq" {
            seq = number(v.bytes(b));
        }
        node.write(OptionItem {
            flags: 0,
            name: k.terminate(buf),
            value: v.terminate(buf),
            next: ptr::null_mut(),
        });
        *tail = node;
        tail = &mut (*node).next;
    }
    let first = parsed.first.terminate(buf);
    let second = parsed.second.terminate(buf);
    let third = parsed.third.terminate(buf);
    let (protocol, line) = if parsed.response {
        (
            first,
            StartLine {
                response: Response {
                    status: number(parsed.second.bytes(b)),
                    reason: third,
                },
            },
        )
    } else {
        (
            third,
            StartLine {
                request: Request {
                    command: first,
                    target: second,
                },
            },
        )
    };
    let length = b.len() - parsed.body;
    out.write(Message {
        kind: parsed.response as u8,
        flags: 6,
        seq,
        protocol,
        options,
        payload: if length == 0 {
            ptr::null_mut()
        } else {
            buf.add(parsed.body)
        },
        payload_len: length as i32,
        buffer: buf,
        line,
    });
    0
}

#[no_mangle]
pub unsafe extern "C" fn pl_rtsp_serialize(
    msg: &Message,
    len: &mut i32,
    alloc: Allocate,
) -> *mut u8 {
    *len = 0;
    let mut bytes = Vec::new();
    let mut append = |s: &[u8]| -> Option<()> {
        if s.len() > i32::MAX as usize - bytes.len() {
            return None;
        }
        bytes.try_reserve(s.len()).ok()?;
        bytes.extend_from_slice(s);
        Some(())
    };
    let string = |p: *mut c_char| {
        if p.is_null() {
            None
        } else {
            Some(CStr::from_ptr(p).to_bytes())
        }
    };
    let ok = (|| {
        if msg.kind == 0 {
            append(string(msg.line.request.command)?)?;
            append(b" ")?;
            append(string(msg.line.request.target)?)?;
            append(b" ")?;
            append(string(msg.protocol)?)?;
        } else {
            append(string(msg.protocol)?)?;
            append(b" ")?;
            append(msg.line.response.status.to_string().as_bytes())?;
            append(b" ")?;
            append(string(msg.line.response.reason)?)?;
        }
        append(b"\r\n")?;
        let mut opt = msg.options;
        while let Some(item) = opt.as_ref() {
            append(string(item.name)?)?;
            append(b": ")?;
            append(string(item.value)?)?;
            append(b"\r\n")?;
            opt = item.next;
        }
        append(b"\r\n")?;
        if msg.payload_len < 0 {
            return None;
        }
        if !msg.payload.is_null() {
            append(slice::from_raw_parts(msg.payload, msg.payload_len as usize))?;
        }
        Some(())
    })();
    if ok.is_none() {
        return ptr::null_mut();
    }
    let result = alloc(bytes.len() + 1).cast::<u8>();
    if !result.is_null() {
        ptr::copy_nonoverlapping(bytes.as_ptr(), result, bytes.len());
        *result.add(bytes.len()) = 0;
        *len = bytes.len() as i32;
    }
    result
}


#[cfg(test)]
mod tests {
    use super::parse;

    #[test]
    fn parser_limits_headers_without_counting_body_bytes() {
        let mut msg = b"RTSP/1.0 200 OK\r\n".to_vec();
        for _ in 0..128 {
            msg.extend_from_slice(b"X: value\r\n");
        }
        msg.extend_from_slice(b"\r\n");
        let mut with_body = msg.clone();
        with_body.resize(32768, 0);
        assert_eq!(parse(&with_body).unwrap().body, msg.len());
        msg.truncate(msg.len() - 2);
        msg.extend_from_slice(b"Extra: value\r\n\r\n");
        assert!(parse(&msg).is_err());
        let mut long = b"RTSP/1.0 200 OK\r\nX: ".to_vec();
        long.resize(16382, b'a');
        long.extend_from_slice(b"\r\n\r\n");
        assert!(parse(&long).is_err());
    }
}
