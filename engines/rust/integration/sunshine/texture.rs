use std::ffi::c_void;
use std::ptr::{null, null_mut};
use windows::core::Interface;
use windows::Win32::Graphics::{
    Direct3D11::{ID3D11DeviceContext, D3D11_VIEWPORT},
    Dxgi::IDXGIKeyedMutex,
};

type Com = *mut c_void;

// All interfaces are borrowed for the duration of one synchronous encode call.
#[repr(C)]
pub struct Conversion {
    ctx: Com,
    mutex: Com,
    source: Com,
    black: Com,
    y_target: Com,
    y_vs: Com,
    y_ps: Com,
    uv_target: Com,
    uv_vs: Com,
    uv_ps: Com,
    y: *const D3D11_VIEWPORT,
    uv: *const D3D11_VIEWPORT,
    clear_y: *const D3D11_VIEWPORT,
    clear_uv: *const D3D11_VIEWPORT,
    planes: u32,
}

#[no_mangle]
pub unsafe extern "C" fn pl_convert_texture(p: &Conversion) -> i32 {
    let (Some(ctx), Some(mutex)) = (
        ID3D11DeviceContext::from_raw_borrowed(&p.ctx),
        IDXGIKeyedMutex::from_raw_borrowed(&p.mutex),
    ) else {
        return -1;
    };
    let api = ctx.vtable();
    let sync = mutex.vtable();
    // WAIT_TIMEOUT and WAIT_ABANDONED are positive HRESULTs and do not grant ownership.
    let status = (sync.AcquireSync)(p.mutex, 0, u32::MAX).0;
    if status != 0 {
        return status;
    }
    let draw_plane = |target, vs, ps, viewports, count| {
        (api.OMSetRenderTargets)(p.ctx, 1, &target, null_mut());
        (api.VSSetShader)(p.ctx, vs, null(), 0);
        (api.PSSetShader)(p.ctx, ps, null(), 0);
        (api.RSSetViewports)(p.ctx, count, viewports);
        (api.Draw)(p.ctx, 3 * count, 0);
    };
    let draw = |source, y, uv| {
        (api.PSSetShaderResources)(p.ctx, 0, 1, &source);
        draw_plane(p.y_target, p.y_vs, p.y_ps, y, p.planes);
        if !p.uv_target.is_null() {
            draw_plane(p.uv_target, p.uv_vs, p.uv_ps, uv, 1);
        }
    };
    if !p.black.is_null() {
        draw(p.black, p.clear_y, p.clear_uv);
    }
    draw(p.source, p.y, p.uv);
    (api.PSSetShaderResources)(p.ctx, 0, 1, &null_mut());
    (sync.ReleaseSync)(p.mutex, 0).0
}
