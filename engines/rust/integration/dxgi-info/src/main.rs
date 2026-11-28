use windows::core::Result;
use windows::Win32::Graphics::Dxgi::{CreateDXGIFactory1, IDXGIFactory1, DXGI_ERROR_NOT_FOUND};
use windows::Win32::UI::HiDpi::{
    SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
};

fn wide_name(value: &[u16]) -> String {
    let length = value.iter().position(|&c| c == 0).unwrap_or(value.len());
    String::from_utf16_lossy(&value[..length])
}

fn main() -> Result<()> {
    // DXGI returns physical desktop coordinates when the process is DPI-aware.
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        let factory: IDXGIFactory1 = CreateDXGIFactory1()?;
        for index in 0.. {
            let adapter = match factory.EnumAdapters1(index) {
                Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
                other => other?,
            };
            let info = adapter.GetDesc1()?;
            println!("====== ADAPTER =====");
            println!("Device Name       : {}", wide_name(&info.Description));
            println!("Device Vendor ID  : 0x{:08X}", info.VendorId);
            println!("Device Device ID  : 0x{:08X}", info.DeviceId);
            println!(
                "Device Video Mem  : {} MiB",
                info.DedicatedVideoMemory >> 20
            );
            println!(
                "Device Sys Mem    : {} MiB",
                info.DedicatedSystemMemory >> 20
            );
            println!("Share Sys Mem     : {} MiB", info.SharedSystemMemory >> 20);
            for index in 0.. {
                let output = match adapter.EnumOutputs(index) {
                    Err(error) if error.code() == DXGI_ERROR_NOT_FOUND => break,
                    other => other?,
                };
                if index == 0 {
                    println!("\n    ====== OUTPUT ======");
                }
                let desc = output.GetDesc()?;
                let rect = desc.DesktopCoordinates;
                println!("    Output Name       : {}", wide_name(&desc.DeviceName));
                println!(
                    "    AttachedToDesktop : {}",
                    if desc.AttachedToDesktop.as_bool() {
                        "yes"
                    } else {
                        "no"
                    }
                );
                println!(
                    "    Resolution        : {}x{}",
                    rect.right - rect.left,
                    rect.bottom - rect.top
                );
            }
            println!();
        }
    }
    Ok(())
}
