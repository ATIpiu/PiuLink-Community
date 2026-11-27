"""Replace Sunshine's audio RTP, cipher IV and repair-header writers."""

def integrate(code):
    start = code.index("    audio_packet.rtp.header = 0x80;")
    end = code.index("    // Audio traffic", start)
    code = code[:start] + """    static_assert(sizeof(audio_packet_t) == 12 && sizeof(audio_fec_packet_t) == 24);

""" + code[end:]
    start = code.index("      *(std::uint32_t *) iv.data()")
    end = code.index("      auto &shards_p", start)
    code = code[:start] + """      pl_audio_iv(reinterpret_cast<uint8_t*>(iv.data()), session->audio.avRiKeyId, sequenceNumber);

""" + code[end:]
    start = code.index("      audio_packet.rtp.sequenceNumber =")
    end = code.index("      auto peer_address",start)
    code = code[:start] + """      bool block_complete = pl_audio_data(reinterpret_cast<uint8_t*>(&audio_packet),
          &session->audio.sequenceNumber, &session->audio.timestamp, session->config.audio.packetDuration);

""" + code[end:]
    start = code.index("        // initialize the FEC header")
    end = code.index("          if (pl_audio_encode",start)
    code = code[:start] + "        if (block_complete) {\n" + code[end:]
    start = code.index("            fec_packet.rtp.sequenceNumber =")
    end = code.index("            auto send_info",start)
    code = code[:start] + """            pl_audio_parity(reinterpret_cast<uint8_t*>(&fec_packet), sequenceNumber, timestamp,
                session->config.audio.packetDuration, x);

""" + code[end:]
    start = code.index("      session->audio.fec_packet.rtp.header =")
    end = code.index("      session->audio.cipher =",start)
    return code[:start] + code[end:]
