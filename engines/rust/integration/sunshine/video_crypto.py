from pathlib import Path

def integrate(code):
    code = code.replace("std::optional<crypto::cipher::gcm_t> cipher;",
                        "std::unique_ptr<PlVideoCipher, decltype(&pl_video_cipher_free)> cipher {nullptr, pl_video_cipher_free};")
    start=code.index("        session->video.cipher = crypto::cipher::gcm_t")
    end=code.index("        session->video.gcm_iv_counter = 0;",start)+len("        session->video.gcm_iv_counter = 0;")
    code=code[:start]+"""        session->video.cipher.reset(pl_video_cipher_new(launch_session.gcm_key.data(),launch_session.gcm_key.size()));
        if (!session->video.cipher) return nullptr;"""+code[end:]
    start=code.index("              // We use the deterministic IV",code.index("            // Encrypt this shard"))
    end=code.index("            }",start)
    code=code[:start]+"""              static_assert(sizeof(video_packet_enc_prefix_t)==32);
              if (!pl_video_cipher_seal(session->video.cipher.get(), packet->frame_index(),
                      reinterpret_cast<uint8_t*>(shards.prefix(x)), reinterpret_cast<uint8_t*>(inspect), blocksize))
                throw std::runtime_error("Cannot encrypt video shard");
"""+code[end:]
    begin=code.index("  void videoBroadcastThread(")
    end=code.index("  void audioBroadcastThread(",begin)
    code=code[:begin]+code[begin:end].replace("    crypto::aes_t iv(12);","")+code[end:]
    return code.replace("      std::uint64_t gcm_iv_counter;", "")
