from pathlib import Path

def integrate(directory):
    header=directory/"video.h"
    code=header.read_text()
    needle="    packet_raw_avcodec() {"
    header.write_text(code.replace(needle,"    explicit packet_raw_avcodec(AVPacket *owned): av_packet(owned) {}\n"+needle))
    source=directory/"video.cpp"
    code=source.read_text()
    start=code.index("  class avcodec_encode_session_t:")
    code=code[:start]+Path(__file__).with_suffix(".inc").read_text()+"\n"+code[start:]
    start=code.index("      if (avcodec_send_frame(")
    end=code.index("      // Order matters",start)
    code=code[:start]+"""        pl_avcodec_encode(avcodec_ctx.get(),nullptr,nullptr,&rustAvOps,nullptr);

"""+code[end:]
    start=code.index("    // send the frame to the encoder")
    end=code.index("      if (av_packet->flags & AV_PKT_FLAG_KEY)",start)
    code=code[:start]+"""    auto publish = [&](AVPacket *av_packet) {
      // Take ownership even if the wrapper allocation fails.
      auto packet = std::unique_ptr<packet_raw_avcodec>(new (std::nothrow) packet_raw_avcodec(av_packet));
      if (!packet) { av_packet_free(&av_packet); return false; }
"""+code[end:]
    start=code.index("      packets->raise(std::move(packet));",start)
    end=code.index("\n  }",start)
    code=code[:start]+"""      packets->raise(std::move(packet));
      return true;
    };
    int result = pl_avcodec_encode(ctx.get(),frame,&publish,&rustAvOps,[](void *opaque,AVPacket *packet) {
      try { return (*static_cast<decltype(publish)*>(opaque))(packet); }
      catch (const std::exception &e) { BOOST_LOG(error) << e.what(); return false; }
    });
    if (result < 0) {
      char message[AV_ERROR_MAX_STRING_SIZE] {};
      BOOST_LOG(error) << "Rust avcodec encode: " << av_make_error_string(message,sizeof(message),result);
    }
    return result;"""+code[end:]
    source.write_text(code)
