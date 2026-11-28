use std::{ffi::c_void, ptr, thread, time::Duration};
use windows::{
    core::{IUnknown, Interface, GUID, HRESULT},
    Win32::{
        Foundation::{E_INVALIDARG, E_POINTER},
        Graphics::{
            Direct3D11::{ID3D11Device, D3D11_BIND_RENDER_TARGET, D3D11_TEXTURE2D_DESC},
            Dxgi::{
                Common::{DXGI_FORMAT, DXGI_SAMPLE_DESC},
                IDXGIOutput, IDXGIOutput1, IDXGIOutput5,
            },
        },
    },
};

fn duplicate_with_retry<T>(
    mut attempt: impl FnMut() -> windows::core::Result<T>,
    mut delay: impl FnMut(),
) -> windows::core::Result<T> {
    let first = attempt();
    if first.is_ok() {
        return first;
    }
    delay();
    attempt()
}

// The caller owns the returned COM reference. All input references are borrowed.
#[no_mangle]
pub unsafe extern "C" fn pl_dxgi_duplicate(
    output: *mut c_void,
    device: *mut c_void,
    formats: *const DXGI_FORMAT,
    count: u32,
    sync_desktop: extern "C" fn(),
    result: &mut *mut c_void,
) -> HRESULT {
    *result = ptr::null_mut();
    let (Some(output), Some(device)) = (
        IDXGIOutput::from_raw_borrowed(&output),
        IUnknown::from_raw_borrowed(&device),
    ) else {
        return E_POINTER;
    };
    let pause = || thread::sleep(Duration::from_millis(200));
    let duplication = if let Ok(output5) = output.cast::<IDXGIOutput5>() {
        if count == 0 || formats.is_null() {
            return E_INVALIDARG;
        }
        let formats = std::slice::from_raw_parts(formats, count as usize);
        duplicate_with_retry(
            || {
                sync_desktop();
                output5.DuplicateOutput1(device, 0, formats)
            },
            pause,
        )
    } else {
        match output.cast::<IDXGIOutput1>() {
            Ok(output1) => duplicate_with_retry(
                || {
                    sync_desktop();
                    output1.DuplicateOutput(device)
                },
                pause,
            ),
            Err(e) => Err(e),
        }
    };
    match duplication {
        Ok(value) => {
            *result = value.into_raw();
            HRESULT(0)
        }
        Err(e) => e.code(),
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_nv_input_texture(
    device: *mut c_void,
    width: u32,
    height: u32,
    format: DXGI_FORMAT,
    result: &mut *mut c_void,
) -> HRESULT {
    *result = ptr::null_mut();
    let Some(device) = ID3D11Device::from_raw_borrowed(&device) else {
        return E_POINTER;
    };
    let desc = D3D11_TEXTURE2D_DESC {
        Width: width,
        Height: height,
        MipLevels: 1,
        ArraySize: 1,
        Format: format,
        SampleDesc: DXGI_SAMPLE_DESC {
            Count: 1,
            Quality: 0,
        },
        BindFlags: D3D11_BIND_RENDER_TARGET.0 as u32,
        ..Default::default()
    };
    let mut texture = None;
    match device.CreateTexture2D(&desc, None, Some(&mut texture)) {
        Ok(()) => match texture {
            Some(t) => {
                *result = t.into_raw();
                HRESULT(0)
            }
            None => E_POINTER,
        },
        Err(e) => e.code(),
    }
}

type CountGuids = unsafe extern "system" fn(*mut c_void, *mut u32) -> i32;
type ReadGuids = unsafe extern "system" fn(*mut c_void, *mut GUID, u32, *mut u32) -> i32;

#[no_mangle]
pub unsafe extern "C" fn pl_nv_supports_codec(
    encoder: *mut c_void,
    codec: &GUID,
    count_guids: CountGuids,
    read_guids: ReadGuids,
    supported: &mut bool,
) -> i32 {
    *supported = false;
    let mut count = 0;
    let status = count_guids(encoder, &mut count);
    if status != 0 || count == 0 {
        return status;
    }
    let mut guids = Vec::new();
    if guids.try_reserve_exact(count as usize).is_err() {
        return 10;
    } // NV_ENC_ERR_OUT_OF_MEMORY
    guids.resize(count as usize, GUID::zeroed());
    let mut written = 0;
    let status = read_guids(encoder, guids.as_mut_ptr(), count, &mut written);
    if status == 0 {
        let Some(list) = guids.get(..written as usize) else {
            return 20;
        }; // NV_ENC_ERR_GENERIC
        *supported = list.contains(codec);
    }
    status
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn desktop_switch_retries_once() {
        let mut attempts = 0;
        let mut pauses = 0;
        let result = duplicate_with_retry(
            || {
                attempts += 1;
                if attempts == 1 {
                    Err(E_POINTER.into())
                } else {
                    Ok(7)
                }
            },
            || pauses += 1,
        );
        assert_eq!((result.unwrap(), attempts, pauses), (7, 2, 1));
        assert!(duplicate_with_retry(|| Ok(()), || panic!("successful duplication slept")).is_ok());
    }
}

#[repr(C)]
#[derive(Debug, PartialEq, Eq)]
pub struct FrameActions {
    save: u32,
    output: u32,
}

#[no_mangle]
pub extern "C" fn pl_dxgi_plan_frame(
    known_format: bool,
    fresh: bool,
    cursor: bool,
    saved_image: bool,
    saved_surface: bool,
) -> FrameActions {
    // ABI values correspond to the C++ operations that own the shared image pool.
    let (save, output) = match (known_format, fresh, cursor, saved_image, saved_surface) {
        (false, ..) => (0, 2),
        (_, true, true, ..) => (4, 1),
        (_, true, false, ..) => (3, 0),
        (_, _, true, true, _) => (2, 1),
        (_, _, true, _, true) => (0, 1),
        (_, _, false, true, _) => (0, 0),
        (_, _, false, _, true) => (1, 0),
        _ => (0, 2),
    };
    FrameActions { save, output }
}

type ReleaseObject = unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32;
type CloseEncoder = unsafe extern "system" fn(*mut c_void) -> i32;
#[repr(C)]
pub struct NvCleanup {
    encoder: *mut c_void,
    buffer: *mut c_void,
    event: *mut c_void,
    input: *mut c_void,
    free_buffer: ReleaseObject,
    unregister_event: ReleaseObject,
    unregister_input: ReleaseObject,
    close: CloseEncoder,
}

#[no_mangle]
pub unsafe extern "C" fn pl_nv_close(ops: &NvCleanup, errors: &mut [i32; 4]) {
    *errors = [0; 4];
    if ops.encoder.is_null() {
        return;
    }
    for (index, (object, release)) in [
        (ops.buffer, ops.free_buffer),
        (ops.event, ops.unregister_event),
        (ops.input, ops.unregister_input),
    ]
    .into_iter()
    .enumerate()
    {
        if !object.is_null() {
            errors[index] = release(ops.encoder, object);
        }
    }
    errors[3] = (ops.close)(ops.encoder);
}

#[cfg(test)]
mod lifecycle_tests {
    use super::*;
    use std::cell::RefCell;

    #[test]
    fn pointer_only_frames_reuse_the_right_gpu_resource() {
        let action = |known, fresh, cursor, image, surface| {
            let a = pl_dxgi_plan_frame(known, fresh, cursor, image, surface);
            (a.save, a.output)
        };
        assert_eq!(action(false, true, true, false, false), (0, 2));
        assert_eq!(action(true, true, true, false, false), (4, 1));
        assert_eq!(action(true, true, false, false, false), (3, 0));
        assert_eq!(action(true, false, true, true, false), (2, 1));
        assert_eq!(action(true, false, false, false, true), (1, 0));
        assert_eq!(action(true, false, true, false, true), (0, 1));
        assert_eq!(action(true, false, false, true, false), (0, 0));
        assert_eq!(action(true, false, true, false, false), (0, 2));
    }

    #[test]
    fn cleanup_continues_after_failure_and_skips_unallocated_resources() {
        thread_local! { static RELEASED: RefCell<Vec<usize>> = const {RefCell::new(Vec::new())}; }
        unsafe extern "system" fn release(_: *mut c_void, resource: *mut c_void) -> i32 {
            RELEASED.with(|calls| calls.borrow_mut().push(resource as usize));
            if resource as usize == 1 {
                7
            } else {
                0
            }
        }
        unsafe extern "system" fn close(_: *mut c_void) -> i32 {
            RELEASED.with(|calls| calls.borrow_mut().push(4));
            0
        }
        let ops = NvCleanup {
            encoder: 1usize as _,
            buffer: 1usize as _,
            event: ptr::null_mut(),
            input: 3usize as _,
            free_buffer: release,
            unregister_event: release,
            unregister_input: release,
            close,
        };
        let mut errors = [0; 4];
        unsafe { pl_nv_close(&ops, &mut errors) };
        assert_eq!(errors, [7, 0, 0, 0]);
        RELEASED.with(|calls| assert_eq!(*calls.borrow(), vec![1, 3, 4]));
    }
}
