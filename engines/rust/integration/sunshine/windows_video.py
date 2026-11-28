"""Replace Windows DXGI duplication and native NVENC resource setup in the source overlay."""
from pathlib import Path


def integrate(src: Path):
    base = src / "platform/windows/display_base.cpp"
    code = base.read_text()
    begin = code.index("    HRESULT status;", code.index("int duplication_t::init("))
    end = code.index("    DXGI_OUTDUPL_DESC dup_desc;", begin)
    base.write_text(code[:begin] + """    display->capture_format = DXGI_FORMAT_UNKNOWN;
    const auto formats = display->get_supported_capture_formats();
    void *handle = nullptr;
    const HRESULT status = pl_dxgi_duplicate(display->output.get(), display->device.get(),
        formats.data(), static_cast<uint32_t>(formats.size()), [] { syncThreadDesktop(); }, &handle);
    if (FAILED(status)) {
      BOOST_LOG(warning) << "Rust DXGI duplication failed: " << util::hex(status).to_string_view();
      return -1;
    }
    dup.reset(static_cast<IDXGIOutputDuplication *>(handle));
    BOOST_LOG(info) << "Rust DXGI duplication opened";

""" + code[end:])
    unit = src / "nvenc/nvenc_d3d11_native.cpp"
    code = unit.read_text().replace("namespace nvenc {", """extern "C" HRESULT pl_nv_input_texture(void *, uint32_t, uint32_t, DXGI_FORMAT, void **);

namespace nvenc {""", 1)
    begin = code.index("      D3D11_TEXTURE2D_DESC desc")
    end = code.index("\n    }", begin)
    unit.write_text(code[:begin] + """      void *texture = nullptr;
      const HRESULT status = pl_nv_input_texture(d3d_device.GetInterfacePtr(), encoder_params.width,
          encoder_params.height, dxgi_format_from_nvenc_format(encoder_params.buffer_format), &texture);
      if (FAILED(status)) {
        BOOST_LOG(error) << "Rust NVENC input texture creation failed: " << util::hex(status).to_string_view();
        return false;
      }
      d3d_input_texture.Attach(static_cast<ID3D11Texture2D *>(texture));
      BOOST_LOG(debug) << "Rust NVENC input texture created";""" + code[end:])
    unit = src / "nvenc/nvenc_base.cpp"
    code = unit.read_text().replace("namespace nvenc {", """extern "C" int pl_nv_supports_codec(void *, const GUID *, PNVENCGETENCODEGUIDCOUNT, PNVENCGETENCODEGUIDS, bool *);

namespace nvenc {""", 1)
    begin = code.index("    uint32_t encode_guid_count = 0;")
    end = code.index("    NV_ENC_INITIALIZE_PARAMS init_params", begin)
    code = code[:begin] + code[end:]
    begin = code.index("      auto search_predicate")
    end = code.index("        BOOST_LOG(error)", begin)
    unit.write_text(code[:begin] + """      bool supported = false;
      const auto status = static_cast<NVENCSTATUS>(pl_nv_supports_codec(encoder, &init_params.encodeGUID,
          nvenc->nvEncGetEncodeGUIDCount, nvenc->nvEncGetEncodeGUIDs, &supported));
      if (nvenc_failed(status) || !supported) {
""" + code[end:])

    unit = src / "platform/windows/display_vram.cpp"
    code = unit.read_text()
    anchor = "namespace platf::dxgi {"
    code = code.replace(anchor, """struct PlFrameActions { uint32_t save, output; };
extern "C" PlFrameActions pl_dxgi_plan_frame(bool, bool, bool, bool, bool);
""" + anchor, 1)
    begin = code.index("    auto last_frame_action = lfa::nothing;")
    end = code.index("    auto create_surface =", begin)
    unit.write_text(code[:begin] + """    static_assert(static_cast<uint32_t>(lfa::copy_src_to_surface) == 4);
    static_assert(static_cast<uint32_t>(ofa::dummy_fallback) == 2);
    const auto actions = pl_dxgi_plan_frame(capture_format != DXGI_FORMAT_UNKNOWN, !!src, blend_mouse_cursor_flag,
        std::holds_alternative<std::shared_ptr<platf::img_t>>(last_frame_variant),
        std::holds_alternative<texture2d_t>(last_frame_variant));
    const auto last_frame_action = static_cast<lfa>(actions.save);
    const auto out_frame_action = static_cast<ofa>(actions.output);

""" + code[end:])
    unit = src / "nvenc/nvenc_base.cpp"
    code = unit.read_text()
    begin = code.index("    if (output_bitstream)", code.index("  void nvenc_base::destroy_encoder()"))
    end = code.index("    encoder_state = {};", begin)
    code = code[:begin] + """    if (encoder) {
      NV_ENC_EVENT_PARAMS event {NV_ENC_EVENT_PARAMS_VER};
      event.completionEvent = async_event_handle;
      const PlNvCleanup resources {encoder, output_bitstream, output_bitstream && async_event_handle ? &event : nullptr, registered_input_buffer,
        nvenc->nvEncDestroyBitstreamBuffer, nvenc->nvEncUnregisterAsyncEvent, nvenc->nvEncUnregisterResource, nvenc->nvEncDestroyEncoder};
      int errors[4] {};
      pl_nv_close(&resources, errors);
      for (int i = 0; i < 4; ++i) {
        if (errors[i]) BOOST_LOG(error) << "Rust NVENC release stage " << i << " failed: " << errors[i];
      }
    }
    output_bitstream = nullptr;
    registered_input_buffer = nullptr;
    encoder = nullptr;
""" + code[end:]
    unit.write_text(code.replace('extern "C" int pl_nv_supports_codec', """struct PlNvCleanup {
  void *encoder, *buffer, *event, *input;
  PNVENCDESTROYBITSTREAMBUFFER free_buffer;
  PNVENCUNREGISTERASYNCEVENT unregister_event;
  PNVENCUNREGISTERRESOURCE unregister_input;
  PNVENCDESTROYENCODER close;
};
extern "C" void pl_nv_close(const PlNvCleanup *, int *);
extern "C" int pl_nv_supports_codec""", 1))
