from pathlib import Path


def integrate(src):
    unit = src / "platform/windows/display_vram.cpp"
    code = unit.read_text()
    start = code.index("  class d3d_base_encode_device final")
    code = code[:start] + Path(__file__).with_suffix(".h").read_text() + "\n" + code[start:]
    start = code.index("        // Acquire encoder mutex", start)
    end = code.index("\n      }\n\n      return 0;",start)
    unit.write_text(code[:start] + """        shader_res_t black;
        if (!rtvs_cleared) black = create_black_texture_for_rtv_clear();
        const bool fp16 = img.format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        const PlConversion conversion {
          device_ctx.get(), img_ctx.encoder_mutex.get(), img_ctx.encoder_input_res.get(), black.get(),
          out_Y_or_YUV_rtv.get(), convert_Y_or_YUV_vs.get(), fp16 ? convert_Y_or_YUV_fp16_ps.get() : convert_Y_or_YUV_ps.get(),
          out_UV_rtv.get(), convert_UV_vs.get(), fp16 ? convert_UV_fp16_ps.get() : convert_UV_ps.get(),
          out_Y_or_YUV_viewports.data(), &out_UV_viewport, out_Y_or_YUV_viewports_for_clear.data(),
          &out_UV_viewport_for_clear, format == DXGI_FORMAT_R16_UINT ? 3u : 1u
        };
        auto status = pl_convert_texture(&conversion);
        if (status != S_OK) {
          BOOST_LOG(error) << "Rust D3D11 texture conversion failed: " << util::hex(status).to_string_view();
          return -1;
        }
        rtvs_cleared = true;""" + code[end:])
