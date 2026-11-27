"""Instrument only an explicitly selected Sunshine test build's first video batch."""
def inject(code):
    anchor = "if (platf::send_batch(batch_info)) {"
    if code.count(anchor) != 1:
        raise RuntimeError("Unexpected Sunshine send path")
    audio = "        platf::send(send_info);\n\n        auto &fec_packet"
    if code.count(audio) != 1:
        raise RuntimeError("Unexpected Sunshine audio send")
    code = code.replace(audio, """        if (sequenceNumber % 200 != 1)
          platf::send(send_info);
        else
          BOOST_LOG(info) << "QA audio drop seq=" << sequenceNumber;

        auto &fec_packet""")
    split = "fecPercentage, false, &layout"
    if code.count(split) != 1:
        raise RuntimeError("Unexpected Sunshine block splitter")
    code = code.replace(split, "fecPercentage, packet->frame_index() % 60 == 0, &layout")
    return code.replace(anchor, """if (blockIndex == 0 && next_shard_to_send == 0 &&
                  current_batch_size >= 4 && shards.data_shards >= 4 &&
                  shards.size() - shards.data_shards >= 2 && packet->frame_index() % 60 == 0) {
                // Send 2, 2, 0, 3... using the actual encrypted shards and parity.
                for (size_t slot = 0; slot < current_batch_size; ++slot) {
                  size_t index = slot < 2 ? 2 : slot == 2 ? 0 : slot;
                  auto info = platf::send_info_t {
                    shards.prefix(index), shards.prefixsize, shards.data(index), shards.blocksize,
                    (uintptr_t) sock.native_handle(), peer_address, session->video.peer.port(),
                    session->localAddress,
                  };
                  if (platf::send(info)) {
                    ++diagnostic_udp_packets;
                    diagnostic_udp_bytes += shards.prefixsize + shards.blocksize;
                  } else {
                    ++diagnostic_send_failures;
                  }
                }
                BOOST_LOG(info) << "QA video impairment: frame=" << packet->frame_index()
                                << " duplicate=2 drop=1 late=0";
              } else """ + anchor)

