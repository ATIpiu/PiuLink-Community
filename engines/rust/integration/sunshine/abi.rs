use piulink_stream_rate::Controller;

// The C++ owner creates one handle per encoder and calls it on its encoding thread.
#[no_mangle]
pub extern "C" fn pl_rate_new() -> *mut Controller {
    Box::into_raw(Box::new(Controller::new(0, 60)))
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_free(handle: *mut Controller) {
    drop(Box::from_raw(handle));
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_reset(handle: *mut Controller, ceiling: u32, fps: u32) {
    (*handle).reset(ceiling, fps);
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_enable(handle: *mut Controller, enabled: u8) {
    (*handle).enable(enabled != 0);
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_ceiling(handle: *mut Controller, rate: u32) {
    (*handle).set_ceiling(rate);
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_applied(handle: *mut Controller, rate: u32) {
    (*handle).applied(rate);
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_observe(handle: *mut Controller, bytes: u64, qp: u32, idr: u8) -> u32 {
    (*handle).observe(bytes, qp, idr != 0).unwrap_or(0)
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_output(handle: *const Controller) -> u32 {
    (*handle).output_kbps()
}
#[no_mangle]
pub unsafe extern "C" fn pl_rate_qp(handle: *const Controller) -> u32 {
    (*handle).average_qp_x100()
}

#[path = "../wire/fec.rs"]
mod fec;
#[path = "../wire/recovery.rs"]
mod recovery;
#[path = "../wire/layout.rs"]
mod layout;
#[path = "../wire/video_header.rs"]
mod video_header;
#[path = "../wire/packetize.rs"]
mod packetize;
#[path = "../wire/keyboard.rs"]
mod keyboard;
#[path = "../wire/rtsp.rs"]
mod rtsp;
#[path = "../wire/pointer.rs"]
mod pointer;

#[path = "../wire/input_batch.rs"]
mod input_batch;

#[path = "../wire/gamepad.rs"]
mod gamepad;

#[path = "../wire/audio_sender.rs"]
mod audio_sender;

#[path = "../wire/video_sender.rs"]
mod video_sender;

#[path = "../wire/session.rs"]
mod session;

#[path = "../wire/host_session.rs"]
mod host_session;

#[path = "../wire/opus_stream.rs"]
mod opus_stream;

#[cfg(windows)]
mod udp;

#[cfg(windows)]
mod capture;

mod encode;
mod announce;

#[cfg(windows)]
mod wasapi;
#[path = "../wire/rtsp_record.rs"]
mod rtsp_record;
#[path = "../wire/video_cipher.rs"]
mod video_cipher;
#[path = "../wire/media_crypto.rs"]
mod media_crypto;
mod avcodec;
#[cfg(windows)]
mod wgc;
mod pairing;
mod launch;
#[cfg(windows)]
mod texture;
#[path = "../wire/enet.rs"]
mod enet;
mod nv_init;

#[cfg(windows)]
mod windows_video;
