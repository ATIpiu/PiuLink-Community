use std::{ffi::c_void, ptr, slice};
use windows::{core::Interface, Win32::Media::Audio::*};
use windows_sys::Win32::{
    Foundation::{WAIT_OBJECT_0, WAIT_TIMEOUT},
    System::Threading::WaitForSingleObjectEx,
};

const OK: i32 = 0;
const REINIT: i32 = 1;
const TIMEOUT: i32 = 2;
const ERROR: i32 = 4;

#[repr(C)]
pub struct Capture<'a> {
    client: *mut c_void,
    event: *mut c_void,
    channels: u32,
    timeout: u32,
    continuous: bool,
    buffer: *mut f32,
    capacity: usize,
    used: &'a mut usize,
    ctx: *mut c_void,
    changed: extern "C" fn(*mut c_void) -> bool,
    report: extern "C" fn(u32),
}

fn status(hr: i32) -> i32 {
    if hr == AUDCLNT_E_DEVICE_INVALIDATED.0 {
        REINIT
    } else {
        ERROR
    }
}

unsafe fn drain(cap: &mut Capture, client: &IAudioCaptureClient) -> i32 {
    loop {
        match client.GetNextPacketSize() {
            Ok(0) => return OK,
            Err(e) => return status(e.code().0),
            _ => (),
        }
        let (mut raw, mut frames, mut flags) = (ptr::null_mut(), 0, 0);
        let hr = (client.vtable().GetBuffer)(
            client.as_raw(),
            &mut raw,
            &mut frames,
            &mut flags,
            ptr::null_mut(),
            ptr::null_mut(),
        );
        if hr == AUDCLNT_S_BUFFER_EMPTY {
            return OK;
        }
        if hr.is_err() {
            return status(hr.0);
        }
        let total = frames as usize * cap.channels as usize;
        let count = total.min(cap.capacity - *cap.used);
        if flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY.0 as u32 != 0 {
            (cap.report)(1);
        }
        if count < total {
            (cap.report)(2);
        }
        let silent = flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0;
        let valid = silent || count == 0 || !raw.is_null();
        if valid && count != 0 {
            let target = cap.buffer.add(*cap.used);
            if silent {
                ptr::write_bytes(target, 0, count);
            } else {
                ptr::copy_nonoverlapping(raw.cast::<f32>(), target, count);
            }
            *cap.used += count;
        }
        if let Err(e) = client.ReleaseBuffer(frames) {
            return status(e.code().0);
        }
        if !valid {
            return ERROR;
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_wasapi_sample(
    cap: &mut Capture,
    output: *mut f32,
    samples: usize,
) -> i32 {
    if cap.buffer.is_null()
        || output.is_null()
        || samples > cap.capacity
        || *cap.used > cap.capacity
        || !(1..=8).contains(&cap.channels)
    {
        return ERROR;
    }
    let raw = cap.client;
    let Some(client) = IAudioCaptureClient::from_raw_borrowed(&raw) else {
        return ERROR;
    };
    while *cap.used < samples {
        if (cap.changed)(cap.ctx) {
            return REINIT;
        }
        match WaitForSingleObjectEx(cap.event, cap.timeout, 0) {
            WAIT_OBJECT_0 => {
                let code = drain(cap, client);
                if code != OK {
                    return code;
                }
            }
            WAIT_TIMEOUT if cap.continuous => {
                // Preserve any device samples already collected for this output frame.
                slice::from_raw_parts_mut(cap.buffer.add(*cap.used), samples - *cap.used).fill(0.);
                *cap.used = samples;
            }
            WAIT_TIMEOUT => return TIMEOUT,
            _ => return ERROR,
        }
    }
    ptr::copy_nonoverlapping(cap.buffer, output, samples);
    *cap.used -= samples;
    ptr::copy(cap.buffer.add(samples), cap.buffer, *cap.used);
    OK
}
