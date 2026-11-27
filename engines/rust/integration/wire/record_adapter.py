from pathlib import Path
import shutil


def host(source, directory, wire):
    shutil.copy2(wire / "rtsp_record.h",directory)
    source = '#include "rtsp_record.h"\n' + source
    start = source.index("      // We use the deterministic IV",source.index("static void handle_read_encrypted_message("))
    end = source.index('        BOOST_LOG(error) << "Failed to verify RTSP',start)
    source = source[:start]+"""      const auto &key = socket->session->gcm_key;
      if (bytes != payload_length || !pl_rtsp_record(key.data(),key.size(),false,false,0,
          reinterpret_cast<uint8_t *>(header),sizeof(*header)+bytes)) {
"""+source[end:]
    source=source.replace("pl_rtsp_parse(req.get(), (char *) plaintext.data(), (int) plaintext.size(), malloc, free)",
                          "pl_rtsp_parse(req.get(), (char *) header->payload(), (int) bytes, malloc, free)")
    source=source.replace("      auto seq = util::endian::big<std::uint32_t>(header->sequenceNumber);\n","")
    start=source.index("      // We use the deterministic IV",source.index("    if (session.rtsp_cipher)"))
    end=source.index("      // Allocate the message",start)
    source=source[:start]+"""      if (session.rtsp_iv_counter == UINT32_MAX) return;
      const auto seq = ++session.rtsp_iv_counter;
"""+source[end:]
    start=source.index("      // Initialize the message header",start)
    end=source.index("      // Send the full encrypted message",start)
    return source[:start]+"""      if (!pl_rtsp_record(session.gcm_key.data(), session.gcm_key.size(), true, true,
                          seq, message.data(), message.size())) return;
"""+source[end:]


def client(native,wire):
    path=native/"RtspConnection.c"
    shutil.copy2(wire/"rtsp_record.h",native)
    source='#include "rtsp_record.h"\n'+path.read_text()
    start=source.index("    char* serializedMessage;",source.index("static char* sealRtspMessage("))
    end=source.index("// Send RTSP message and get response over ENet",start)
    source=source[:start]+"""    int len=0;
    char *plain=serializeRtspMessage(request,&len);
    if (!plain) return NULL;
    if (!encryptedRtspEnabled) { *messageLen=len; return plain; }
    if (len<=0 || len>INT32_MAX-24 || encryptionSequenceNumber==UINT32_MAX) { free(plain); return NULL; }
    uint8_t *wire=malloc(len+24);
    if (wire) memcpy(wire+24,plain,len);
    free(plain);
    if (!wire) return NULL;
    if (!pl_rtsp_record((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
                        false,true,++encryptionSequenceNumber,wire,len+24)) { free(wire); return NULL; }
    *messageLen=len+24;
    return (char*)wire;
}
static bool unsealRtspMessage(char *wire,int len,PRTSP_MESSAGE response) {
    if (len<=0) return false;
    if (encryptedRtspEnabled) {
        if (!pl_rtsp_record((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
                            true,false,0,(uint8_t*)wire,len)) return false;
        wire+=24;
        len-=24;
    }
    return parseRtspMessage(response,wire,len)==RTSP_ERROR_SUCCESS;
}

"""+source[end:]
    start=source.index("static bool transactRtspMessageTcp(")
    end=source.index("static bool transactRtspMessage(",start)
    source=source[:start]+(wire.parent/"android/rtsp_tcp.inc").read_text()+"\n"+source[end:]
    for unused in ("static SOCKET sock = INVALID_SOCKET;","static PPLT_CRYPTO_CONTEXT encryptionCtx;",
                   "static PPLT_CRYPTO_CONTEXT decryptionCtx;","    encryptionCtx = PltCreateCryptoContext();",
                   "    decryptionCtx = PltCreateCryptoContext();","    PltDestroyCryptoContext(encryptionCtx);",
                   "    PltDestroyCryptoContext(decryptionCtx);","    decryptionCtx = encryptionCtx = NULL;"):
        source=source.replace(unused+"\n","")
    path.write_text(source)
