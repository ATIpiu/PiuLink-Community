use std::{
    ffi::c_void,
    ptr,
    sync::{Arc, Condvar, Mutex},
    time::Duration,
};
use windows::{
    core::{factory, IInspectable, Interface, Result as WinResult},
    Foundation::{TimeSpan, TypedEventHandler},
    Graphics::{
        Capture::*,
        DirectX::{Direct3D11::IDirect3DDevice, DirectXPixelFormat},
    },
    Win32::{
        Graphics::{
            Direct3D11::{ID3D11Device, ID3D11Texture2D},
            Dxgi::IDXGIDevice,
            Gdi::HMONITOR,
        },
        System::WinRT::{
            Direct3D11::{CreateDirect3D11DeviceFromDXGIDevice, IDirect3DDxgiInterfaceAccess},
            Graphics::Capture::IGraphicsCaptureItemInterop,
        },
    },
};
#[derive(Default)]
struct Pending {
    latest: Option<Direct3D11CaptureFrame>,
    closed: bool,
}
#[derive(Default)]
struct Inbox {
    pending: Mutex<Pending>,
    ready: Condvar,
}
fn close(frame: Option<Direct3D11CaptureFrame>) {
    if let Some(frame) = frame {
        let _ = frame.Close();
    }
}
pub struct Wgc {
    session: GraphicsCaptureSession,
    pool: Direct3D11CaptureFramePool,
    token: i64,
    inbox: Arc<Inbox>,
    active: Option<Direct3D11CaptureFrame>,
}
impl Wgc {
    unsafe fn start(device: &ID3D11Device, monitor: HMONITOR, format: i32) -> WinResult<Self> {
        let dxgi: IDXGIDevice = device.cast()?;
        let surface: IDirect3DDevice = CreateDirect3D11DeviceFromDXGIDevice(&dxgi)?.cast()?;
        let interop: IGraphicsCaptureItemInterop =
            factory::<GraphicsCaptureItem, IGraphicsCaptureItemInterop>()?;
        let item: GraphicsCaptureItem = interop.CreateForMonitor(monitor)?;
        let pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
            &surface,
            DirectXPixelFormat(format),
            2,
            item.Size()?,
        )?;
        let session = pool.CreateCaptureSession(&item)?;
        let inbox = Arc::new(Inbox::default());
        let producer = inbox.clone();
        let token = pool.FrameArrived(&TypedEventHandler::<
            Direct3D11CaptureFramePool,
            IInspectable,
        >::new(move |sender, _| {
            let Some(pool) = sender.as_ref() else {
                return Ok(());
            };
            if let Ok(frame) = pool.TryGetNextFrame() {
                let mut state = producer.pending.lock().unwrap_or_else(|p| p.into_inner());
                let retired = if state.closed {
                    Some(frame)
                } else {
                    state.latest.replace(frame)
                };
                drop(state);
                close(retired);
                producer.ready.notify_one();
            }
            Ok(())
        }))?;
        let state = Self {
            session,
            pool,
            token,
            inbox,
            active: None,
        };
        // Unsupported optional properties leave the OS defaults in effect.
        let _ = state.session.SetIsBorderRequired(false);
        let _ = state
            .session
            .SetMinUpdateInterval(TimeSpan { Duration: 40_000 });
        state.session.StartCapture()?;
        Ok(state)
    }
}
impl Drop for Wgc {
    fn drop(&mut self) {
        let pending = {
            let mut s = self.inbox.pending.lock().unwrap_or_else(|p| p.into_inner());
            s.closed = true;
            s.latest.take()
        };
        let _ = self.pool.RemoveFrameArrived(self.token);
        close(pending);
        close(self.active.take());
        let _ = self.session.Close();
        let _ = self.pool.Close();
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_wgc_start(
    device: *mut c_void,
    monitor: *mut c_void,
    format: i32,
    error: &mut i32,
) -> *mut Wgc {
    let Some(dev) = ID3D11Device::from_raw_borrowed(&device) else {
        return ptr::null_mut();
    };
    match Wgc::start(dev, HMONITOR(monitor), format) {
        Ok(w) => Box::into_raw(Box::new(w)),
        Err(e) => {
            *error = e.code().0;
            ptr::null_mut()
        }
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_wgc_free(state: *mut Wgc) {
    if !state.is_null() {
        drop(Box::from_raw(state));
    }
}
#[no_mangle]
pub extern "C" fn pl_wgc_cursor(w: &Wgc, visible: bool) -> i32 {
    match w.session.IsCursorCaptureEnabled() {
        Ok(old) if old == visible => 0,
        Ok(_) if w.session.SetIsCursorCaptureEnabled(visible).is_ok() => 0,
        _ => -1,
    }
}
#[no_mangle]
pub extern "C" fn pl_wgc_release(w: &mut Wgc) -> i32 {
    close(w.active.take());
    0
}
#[no_mangle]
pub unsafe extern "C" fn pl_wgc_next(
    w: &mut Wgc,
    ms: u32,
    out: &mut *mut c_void,
    time: &mut u64,
) -> i32 {
    *out = ptr::null_mut();
    close(w.active.take());
    let state = w.inbox.pending.lock().unwrap_or_else(|p| p.into_inner());
    let (mut state, _) = w
        .inbox
        .ready
        .wait_timeout_while(state, Duration::from_millis(ms.into()), |s| {
            s.latest.is_none() && !s.closed
        })
        .unwrap_or_else(|p| p.into_inner());
    let Some(frame) = state.latest.take() else {
        return 2;
    };
    drop(state);
    let texture = || -> WinResult<(ID3D11Texture2D, i64)> {
        let access: IDirect3DDxgiInterfaceAccess = frame.Surface()?.cast()?;
        Ok((access.GetInterface()?, frame.SystemRelativeTime()?.Duration))
    };
    match texture() {
        Ok((tex, t)) => {
            *out = tex.into_raw();
            *time = t as u64;
            w.active = Some(frame);
            0
        }
        Err(_) => {
            close(Some(frame));
            4
        }
    }
}
