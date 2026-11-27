from pathlib import Path

def host(code):
    # Keep the original driver for legacy GameStream control records.
    code = code.replace("      crypto::cipher::cbc_t cipher;", "      crypto::aes_t key;")
    start = code.index("      session->audio.cipher =")
    end = code.index("      session->audio.ping_payload",start)
    code=code[:start]+"      session->audio.key = launch_session.gcm_key;\n\n"+code[end:]
    code=code.replace("crypto::cipher::cbc_t &cbc)", "const crypto::aes_t &key, size_t capacity)")
    code=code.replace("    // If encryption isn't enabled","    if (plaintext.size() > capacity) return -1;\n    // If encryption isn't enabled",1)
    code=code.replace("return cbc.encrypt(std::string_view {(char *) std::begin(plaintext), plaintext.size()}, destination, &iv);",
                      "return pl_opus_crypt(key.data(),key.size(),iv.data(),true,std::begin(plaintext),plaintext.size(),destination,capacity);")
    code=code.replace("iv, session->audio.cipher);","iv, session->audio.key, session->audio.shards.size()/RTPA_TOTAL_SHARDS);")
    pat="    auto seq = session->control.seq++;"
    code=code.replace(pat,"""    if (session->control.seq > UINT32_MAX) return {};
    uint32_t seq = session->control.seq++;
    if (session->config.encryptionFlagsEnabled & SS_ENC_CONTROL_V2) {
      if (plaintext.size()+24 > tagged_cipher.size()) return {};
      std::memcpy(tagged_cipher.data()+24, plaintext.data(), plaintext.size());
      const auto &key = session->audio.key;
      if (!pl_control_record(key.data(),key.size(),true,true,seq,tagged_cipher.data(),plaintext.size()+24)) return {};
      return {(char*)tagged_cipher.data(), plaintext.size()+24};
    }
""")
    code=code.replace("      std::uint32_t seq;\n","      std::uint64_t seq;\n")
    start=code.index("    auto &iv = session->control.outgoing_iv;")
    end=code.index("    auto packet =",start)
    code=code[:start]+"""    auto &iv = session->control.outgoing_iv;
    iv.resize(16);
    iv[0] = (uint8_t)seq;
"""+code[end:]
    start=code.index("      auto header = (control_encrypted_p)")
    end=code.index("      auto type = ",start)
    code=code[:start]+"""      if (payload.size() < 26 || payload.size() > 65537) return;
      auto header = (control_encrypted_p)(payload.data()-2);
      const auto length = util::endian::little(header->length);
      if (length < 24 || size_t(length)+2 != payload.size()) return;
      std::vector<uint8_t> plaintext;
      if (session->config.encryptionFlagsEnabled & SS_ENC_CONTROL_V2) {
        std::vector<uint8_t> record(payload.size()+2);
        std::memcpy(record.data(),header,record.size());
        const auto &key = session->audio.key;
        if (!pl_control_record(key.data(),key.size(),false,false,0,record.data(),record.size())) {
          session::stop(*session);
          return;
        }
        record.erase(record.begin(),record.begin()+24);
        plaintext=std::move(record);
      } else {
        auto &iv=session->control.incoming_iv;
        iv.resize(16);
        iv[0]=(uint8_t)util::endian::little(header->seq);
        if (session->control.cipher.decrypt({(const char*)header->payload(),size_t(length)-4},plaintext,&iv)) {
          session::stop(*session);
          return;
        }
      }

"""+code[end:]
    return code


def client(native):
    path=native/"AudioStream.c"
    code=path.read_text()
    start=code.index("        if (!PltDecryptMessage(audioDecryptionCtx")
    end=code.index('            Limelog("Failed to decrypt audio',start)
    code=code[:start]+"""        dataLength=pl_opus_crypt((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
            iv,false,(const uint8_t*)(rtp+1),dataLength,decryptedOpusData,sizeof(decryptedOpusData));
        if (dataLength<=0) {
"""+code[end:]
    for line in ["static PPLT_CRYPTO_CONTEXT audioDecryptionCtx;","    audioDecryptionCtx = PltCreateCryptoContext();","    PltDestroyCryptoContext(audioDecryptionCtx);"]:
        code=code.replace(line+"\n","")
    path.write_text(code)
    path=native/"ControlStream.c"
    code='#include "piulink_fec.h"\n'+path.read_text()
    code=code.replace("static uint32_t currentEnetSequenceNumber;","static uint64_t currentEnetSequenceNumber;")
    at=code.index("        encPacket->seq = currentEnetSequenceNumber++;")
    end=code.index("        // enetMutex still locked here",at)
    # Legacy path keeps encryptControlMessage's native two-buffer ABI.
    code=code[:at]+"""        if (currentEnetSequenceNumber>UINT32_MAX) {
            enet_packet_destroy(enetPacket); PltUnlockMutex(&enetMutex); return false;
        }
        encPacket->seq=(uint32_t)currentEnetSequenceNumber++;
        bool ok;
        if (EncryptionFeaturesEnabled & SS_ENC_CONTROL_V2) {
            packet=(PNVCTL_ENET_PACKET_HEADER_V2)(enetPacket->data+24);
            packet->type=LE16(ptype); packet->payloadLength=LE16(paylen);
            if (paylen) memcpy(packet+1,payload,paylen);
            ok=pl_control_record((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
                                 false,true,encPacket->seq,enetPacket->data,enetPacket->dataLength);
        } else {
            if (sizeof(*packet)+(unsigned short)paylen>sizeof(tempBuffer)) {
                enet_packet_destroy(enetPacket); PltUnlockMutex(&enetMutex); return false;
            }
            packet=(PNVCTL_ENET_PACKET_HEADER_V2)tempBuffer;
            packet->type=ptype; packet->payloadLength=paylen;
            if (paylen) memcpy(packet+1,payload,paylen);
            ok=encryptControlMessage(encPacket,packet);
        }
        if (!ok) { enet_packet_destroy(enetPacket); PltUnlockMutex(&enetMutex); return false; }

"""+code[end:]
    at=code.index("    unsigned char iv[16]",code.index("static bool decryptControlMessageToV1("))
    code=code[:at]+"""    if (EncryptionFeaturesEnabled & SS_ENC_CONTROL_V2) {
        *packet=NULL;
        if (encPacketLength<28 || !pl_control_record((const uint8_t*)StreamConfig.remoteInputAesKey,
            sizeof(StreamConfig.remoteInputAesKey),true,false,0,(uint8_t*)encPacket,encPacketLength)) return false;
        const uint8_t *body=(uint8_t*)encPacket+24;
        *packetLength=encPacketLength-26;
        *packet=malloc(*packetLength);
        if (!*packet) return false;
        memcpy(*packet,body,2);
        memcpy((uint8_t*)*packet+2,body+4,*packetLength-2);
        return true;
    }
"""+code[at:]
    code=code.replace("    if (encryptedControlStream) {\n        PNVCTL_ENCRYPTED", "    if (paylen<0 || (paylen && !payload)) return false;\n    if (encryptedControlStream) {\n        PNVCTL_ENCRYPTED",1)
    path.write_text(code)
