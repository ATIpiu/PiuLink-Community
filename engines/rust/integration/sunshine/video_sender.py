"""Install the Rust pacing state and pre/post-FEC RTP writers in Sunshine."""

def integrate(code):
    start = code.index("        // Use around 80% of 1Gbps")
    end = code.index("        // Don't ignore the last ratecontrol", start)
    code = code[:start] + """        static_assert(sizeof(video_packet_raw_t) == 32);
        PlPacing pacing {};
        if (!pl_pacing_init(&pacing, blocksize)) continue;

""" + code[end:]
    start = code.index("        size_t ratecontrol_frame_packets_sent =")
    code = code[:start] + code[code.index("        auto blockIndex",start):]
    start = code.index("            inspect->packet.frameIndex =")
    end = code.index("          }\n\n          frame_fec_latency_logger", start)
    code = code[:start] + """            pl_video_source(reinterpret_cast<uint8_t*>(inspect), lowseq, x, packets,
                packet->frame_index(), blockIndex, fec_blocks_needed - 1);
""" + code[end:]
    start = code.index("            inspect->packet.fecInfo =")
    end = code.index("            // Encrypt this shard",start)
    code = code[:start] + """            pl_video_shard(reinterpret_cast<uint8_t*>(inspect), lowseq, x, timestamp,
                packet->frame_index(), shards.data_shards, shards.percentage, blockIndex, fec_blocks_needed - 1);

""" + code[end:]
    start = code.index("            if (x - next_shard_to_send")
    end = code.index("              batch_info.block_offset",start)
    code = code[:start] + """            size_t current_batch_size = x - next_shard_to_send + 1;
            int64_t due_offset;
            if (pl_pacing_due(&pacing, current_batch_size, x + 1 == shards.size(), &due_offset)) {
              if (due_offset >= 0) {
                auto due = ratecontrol_frame_start + std::chrono::nanoseconds(due_offset);
                auto now = std::chrono::steady_clock::now();
                if (now < due) timer->sleep_for(due - now);
              }
""" + code[end:]
    start = code.index("              ratecontrol_group_packets_sent +=")
    end = code.index("              next_shard_to_send", start)
    code = code[:start] + code[end:]
    start = code.index("          ratecontrol_next_frame_start = ratecontrol_frame_start")
    end = code.index("          frame_network_latency_logger",start)
    return code[:start] + """          ratecontrol_next_frame_start = ratecontrol_frame_start + std::chrono::nanoseconds(pl_pacing_end(&pacing));

""" + code[end:]
