from pathlib import Path


def integrate(src):
    unit = src / "nvenc/nvenc_base.cpp"
    code = unit.read_text()
    start = code.index("namespace nvenc {")
    code = code[:start] + """struct PlNvPlan { uint32_t bitrate, vbv, refs, rows, cols; bool rfi; };
extern "C" bool pl_nv_plan(int,int,int,int,int,bool,bool,int,PlNvPlan*);
using PlNvDriver = int32_t (NVENCAPI *)(void *,void *);
struct PlNvResources { void *encoder, *config, *event, *buffer; PlNvDriver initialize, register_event, create_buffer; };
struct PlNvResult { int32_t stage, error; };
extern "C" PlNvResult pl_nv_initialize(const PlNvResources *);

""" + code[start:]
    start = code.index("    enc_config.rcParams.averageBitRate = client_config.bitrate")
    end = code.index("    auto set_h264_hevc_common_format_config",start)
    code = code[:start] + """    PlNvPlan plan {};
    if (!pl_nv_plan(client_config.videoFormat, client_config.bitrate, client_config.framerate, config.vbv_percentage_increase,
                    client_config.numRefFrames, get_encoder_cap(NV_ENC_CAPS_SUPPORT_MULTIPLE_REF_FRAMES),
                    encoder_params.rfi, client_config.slicesPerFrame, &plan)) return false;
    enc_config.rcParams.averageBitRate = plan.bitrate;
    if (get_encoder_cap(NV_ENC_CAPS_SUPPORT_CUSTOM_VBV_BUF_SIZE)) enc_config.rcParams.vbvBufferSize = plan.vbv;

""" + code[end:]
    start = code.index("      if (client_config.numRefFrames > 0)")
    end = code.index("      encoder_params.ref_frames_in_dpb",start)
    code = code[:start] + """      ref_frames_option = plan.refs;
      encoder_params.rfi = plan.rfi;
""" + code[end:]
    start = code.index("            format_config.numTileRows = std::pow")
    code = code[:start] + """            format_config.numTileRows = plan.rows;
            format_config.numTileColumns = plan.cols;""" + code[code.index("\n          }",start):]
    start = code.index("    if (nvenc_failed(nvenc->nvEncInitializeEncoder(")
    end = code.index("    saved_config = enc_config;",start)
    code = code[:start] + """    NV_ENC_EVENT_PARAMS event {NV_ENC_EVENT_PARAMS_VER};
    event.completionEvent = async_event_handle;
    NV_ENC_CREATE_BITSTREAM_BUFFER buffer {NV_ENC_CREATE_BITSTREAM_BUFFER_VER};
    PlNvResources resources {encoder, &init_params, async_event_handle ? &event : nullptr, &buffer,
        reinterpret_cast<PlNvDriver>(nvenc->nvEncInitializeEncoder),
        reinterpret_cast<PlNvDriver>(nvenc->nvEncRegisterAsyncEvent),
        reinterpret_cast<PlNvDriver>(nvenc->nvEncCreateBitstreamBuffer)};
    const auto result = pl_nv_initialize(&resources);
    if (nvenc_failed(static_cast<NVENCSTATUS>(result.error))) {
      BOOST_LOG(error) << "Rust NVENC initialization stage " << result.stage << ": " << last_nvenc_error_string;
      return false;
    }
    output_bitstream = buffer.bitstreamBuffer;
""" + code[end:]
    start = code.index("    if (async_event_handle) {",start)
    end = code.index("    if (!create_and_register_input_buffer())",start)
    unit.write_text(code[:start] + code[end:])
