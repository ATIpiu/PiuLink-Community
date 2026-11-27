from pathlib import Path


def integrate(directory):
    header=directory/"display.h"
    text=header.read_text()
    start=text.index("    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice",text.index("  class wgc_capture_t {"))
    end=text.index("  public:",start)
    header.write_text(text[:start]+"    void *rust_capture = nullptr;\n\n"+text[end:])
    source=directory/"display_wgc.cpp"
    text=source.read_text()
    start=text.index("  wgc_capture_t::wgc_capture_t()")
    end=text.index("  int display_wgc_ram_t::init(",start)
    source.write_text(text[:start]+"""  extern "C" {
    void *pl_wgc_start(void *, void *, int, int *);
    void pl_wgc_free(void *);
    int pl_wgc_cursor(void *,bool);
    int pl_wgc_release(void *);
    int pl_wgc_next(void *,uint32_t,void **,uint64_t *);
  }
  wgc_capture_t::wgc_capture_t() = default;
  wgc_capture_t::~wgc_capture_t() { pl_wgc_free(rust_capture); }
  int wgc_capture_t::init(display_base_t *display, const ::video::config_t &config) {
    DXGI_OUTPUT_DESC desc {};
    if (FAILED(display->output->GetDesc(&desc))) return -1;
    display->capture_format=config.dynamicRange?DXGI_FORMAT_R16G16B16A16_FLOAT:DXGI_FORMAT_B8G8R8A8_UNORM;
    int hr=0;
    rust_capture=pl_wgc_start(display->device.get(),desc.Monitor,display->capture_format,&hr);
    if (!rust_capture) {BOOST_LOG(error) << "Rust WGC start failed: " << util::hex(hr).to_string_view(); return -1;}
    BOOST_LOG(info) << "Rust Windows.Graphics.Capture session started";
    return 0;
  }
  capture_e wgc_capture_t::next_frame(std::chrono::milliseconds ms, ID3D11Texture2D **texture, uint64_t &ticks) {
    return (capture_e)pl_wgc_next(rust_capture,ms.count(),reinterpret_cast<void **>(texture),&ticks);
  }
  capture_e wgc_capture_t::release_frame() {return (capture_e)pl_wgc_release(rust_capture);}
  int wgc_capture_t::set_cursor_visible(bool value) {return pl_wgc_cursor(rust_capture,value);}

"""+text[end:])
