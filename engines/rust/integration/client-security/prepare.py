"""Apply security checks to the pinned Moonlight clients before media integration."""
import argparse
from pathlib import Path
import shutil
import subprocess

HERE = Path(__file__).resolve().parent
NATIVE = {
    "ios": "moonlight-common/moonlight-common-c/src",
    "macos": "moonlight-common-c/moonlight-common-c/src",
    "harmony": "nativelib/src/main/cpp/moonlight-common-c/src",
}


def replace(text, old, new):
    if text.count(old) != 1:
        raise ValueError(f"Unsupported upstream revision at {old[:70]!r}")
    return text.replace(old, new)


def harden_native(src, platform):
    microseconds = platform != "macos"
    clock = "PltGetMicroseconds()" if microseconds else "PltGetMillis()"
    millis = clock + (" / 1000" if microseconds else "")
    recv_time = "bufferFirstRecvTime" + ("Us" if microseconds else "Ms")

    path = src / "RtpVideoQueue.c"
    text = replace(path.read_text(), "    queue->currentFrameNumber = 1;",
                   f"    queue->{recv_time} = {clock};\n    queue->currentFrameNumber = 1;")
    text = replace(text, "if (isBefore16(nvPacket->frameIndex, queue->currentFrameNumber))", f"""uint32_t ahead = nvPacket->frameIndex - queue->currentFrameNumber;
    if ((int32_t)ahead < 0 || ahead > 512 + ({clock} - queue->{recv_time}) / {2000 if microseconds else 2})""")
    path.write_text(text)

    path = src / "RtpAudioQueue.h"
    path.write_text(replace(path.read_text(), "    PRTPA_FEC_BLOCK blockHead;",
                            "    uint64_t lastBlockTimeMs;\n    PRTPA_FEC_BLOCK blockHead;"))
    path = src / "RtpAudioQueue.c"
    text = replace(path.read_text(), "    // Synchronize the nextRtpSequenceNumber", f"""    if (blockSize == 0 || blockSize > 4096) return NULL;
    uint64_t nowMs = {millis};
    if ((uint16_t)(fecBlockBaseSeqNum - queue->oldestRtpBaseSequenceNumber) > 128 &&
        !(queue->synchronizing && queue->oldestRtpBaseSequenceNumber == 0)) {{
        // Resume on a data block after a prolonged gap. Parity alone cannot move the window.
        if (packet->packetType != RTP_PAYLOAD_TYPE_AUDIO || nowMs - queue->lastBlockTimeMs < 1000)
            return NULL;
        while (queue->blockHead != NULL) {{
            PRTPA_FEC_BLOCK stale = queue->blockHead;
            queue->blockHead = stale->next;
            free(stale);
        }}
        queue->blockTail = NULL;
        queue->nextRtpSequenceNumber = queue->oldestRtpBaseSequenceNumber = fecBlockBaseSeqNum;
        queue->receivedOosData = false;
        queue->synchronizing = false;
    }}
    // Synchronize the nextRtpSequenceNumber""")
    text = replace(text, "        queue->nextRtpSequenceNumber = queue->oldestRtpBaseSequenceNumber = fecBlockBaseSeqNum +", "        queue->lastBlockTimeMs = nowMs;\n        queue->nextRtpSequenceNumber = queue->oldestRtpBaseSequenceNumber = fecBlockBaseSeqNum +")
    text = replace(text, "    block->blockSize = blockSize;", "    queue->lastBlockTimeMs = nowMs;\n    block->blockSize = blockSize;")
    path.write_text(text)

    path = src / "ControlStream.c"
    text = '#include "replay.h"\n' + path.read_text()
    text = replace(text, "static uint32_t currentEnetSequenceNumber;", "static PlReplayWindow receivedControl;\nstatic uint32_t currentEnetSequenceNumber;")
    text = replace(text, "    currentEnetSequenceNumber = 0;", "    memset(&receivedControl, 0, sizeof(receivedControl));\n    currentEnetSequenceNumber = 0;")
    text = replace(text, "encPacketLength < expectedEncLength", "encPacketLength != expectedEncLength")
    start = "    // Now we do an in-place V2"
    text = replace(text, start, """    const unsigned char* inner = (const unsigned char*)*packet;
    if (plaintextLength < 4 || (inner[2] | (inner[3] << 8)) != plaintextLength - 4 ||
        ((EncryptionFeaturesEnabled & SS_ENC_CONTROL_V2) && !pl_accept_authenticated(&receivedControl, encPacket->seq))) {
        free(*packet);
        *packet = NULL;
        return false;
    }

""" + start)
    path.write_text(text)
    shutil.copyfile(HERE / "replay.h", src / "replay.h")

    path = src / "RtspConnection.c"
    text = replace(path.read_text(), "static uint32_t encryptionSequenceNumber;", "static uint32_t lastAuthenticatedReply;\nstatic uint32_t encryptionSequenceNumber;")
    text = replace(text, "    currentSeqNumber = 1;", "    lastAuthenticatedReply = 0;\n    currentSeqNumber = 1;")
    text = replace(text, "        if (!success) {\n            Limelog(\"Failed to decrypt RTSP response", "        if (!success || seq <= lastAuthenticatedReply) {\n            Limelog(\"Failed to authenticate fresh RTSP response")
    text = replace(text, "    }\n    else {\n        decryptedMessage = rawMessage;", "        lastAuthenticatedReply = seq;\n    }\n    else {\n        decryptedMessage = rawMessage;")
    text = replace(text, "    ret = unsealRtspMessage(responseBuffer, offset, response);", """    ret = unsealRtspMessage(responseBuffer, offset, response);
    if (ret && encryptedRtspEnabled) {
        const char* sent = getOptionContent(request->options, "CSeq");
        const char* received = getOptionContent(response->options, "CSeq");
        if (response->type != TYPE_RESPONSE || !sent || !received || strcmp(sent, received) != 0) {
            freeMessage(response);
            ret = false;
        }
    }""")
    text = replace(text, "    err = sendMtuSafe(sock, serializedMessage, messageLen);", f"""    uint64_t startedMs = {millis};
    if (setSocketNonBlocking(sock, true) != 0) goto Exit;
    for (int sent = 0; sent < messageLen;) {{
        uint64_t elapsedMs = ({millis}) - startedMs;
        struct pollfd writable = {{0}};
        writable.fd = sock;
        writable.events = POLLOUT;
        err = elapsedMs >= RTSP_RECEIVE_TIMEOUT_SEC * 1000 ? 0 :
            pollSockets(&writable, 1, RTSP_RECEIVE_TIMEOUT_SEC * 1000 - (int)elapsedMs);
        if (err <= 0) {{
            if (err < 0 && LastSocketError() == EINTR) continue;
            *error = err == 0 ? ETIMEDOUT : LastSocketError();
            goto Exit;
        }}
        int amount = messageLen - sent;
        err = send(sock, serializedMessage + sent, amount > 536 ? 536 : amount, 0);
        if (err < 0 && (LastSocketError() == EINTR || LastSocketError() == EWOULDBLOCK || LastSocketError() == EAGAIN))
            continue;
        if (err <= 0) {{ *error = err < 0 ? LastSocketError() : -1; goto Exit; }}
        sent += err;
    }}
    err = 0;""")
    text = replace(text, "        err = pollSockets(&pfd, 1, RTSP_RECEIVE_TIMEOUT_SEC * 1000);", f"""        uint64_t elapsedMs = ({millis}) - startedMs;
        err = elapsedMs >= RTSP_RECEIVE_TIMEOUT_SEC * 1000 ? 0 :
            pollSockets(&pfd, 1, RTSP_RECEIVE_TIMEOUT_SEC * 1000 - (int)elapsedMs);
        if (err < 0 && LastSocketError() == EINTR) continue;""")
    text = replace(text, "        if (err < 0) {\n            // Error reading", """        if (err < 0 && (LastSocketError() == EINTR || LastSocketError() == EWOULDBLOCK || LastSocketError() == EAGAIN))
            continue;
        if (err < 0) {
            // Error reading""")
    if "#define MAX_RTSP_RESPONSE_SIZE" not in text:
        text = "#define MAX_RTSP_RESPONSE_SIZE (1024 * 1024)\n" + text
        for anchor, size in [("    responseBuffer = malloc(event.packet->dataLength);", "MAX_RTSP_RESPONSE_SIZE"),
                             ("        responseBuffer = extendBuffer(responseBuffer, event.packet->dataLength + offset);", "(size_t)(MAX_RTSP_RESPONSE_SIZE - offset)")]:
            indent = anchor[:len(anchor) - len(anchor.lstrip())]
            text = replace(text, anchor, indent + f"if (event.packet->dataLength > {size}) {{\n" + indent + "    enet_packet_destroy(event.packet);\n" + indent + "    goto Exit;\n" + indent + "}\n" + anchor)
        text = replace(text, "            responseBufferSize = offset + 16384;", "            if (offset >= MAX_RTSP_RESPONSE_SIZE) goto Exit;\n            responseBufferSize = offset + 16384;")
    shutil.copyfile(HERE / "rtsp_record.h", src / "rtsp_record.h")
    text = '#include "rtsp_record.h"\n' + text
    text = replace(text, "    uint64_t startedMs =", "    PlRtspRecord record = {0};\n    uint64_t startedMs =")
    text = replace(text, "            // Done reading", """            if (pl_rtsp_record(&record, (const unsigned char*)responseBuffer, offset, offset, true) < 0)
                goto Exit;
            // Done reading""")
    text = replace(text, "            offset += err;", """            int state = pl_rtsp_record(&record, (const unsigned char*)responseBuffer, offset, offset + err, false);
            if (state < 0) goto Exit;
            offset += err;
            if (state > 0) break;""")
    # Permit EOF at exactly the maximum size on a legacy close-delimited message.
    text = text.replace("offset >= MAX_RTSP_RESPONSE_SIZE", "offset > MAX_RTSP_RESPONSE_SIZE")
    text = replace(text, "responseBufferSize = offset + 16384;",
                   "responseBufferSize = offset == MAX_RTSP_RESPONSE_SIZE ? offset + 1 : offset + 16384;")
    path.write_text(text)

    # Also bound the native parser used by the Harmony fork and by legacy builds.
    path = src / "RtspParser.c"
    text = path.read_text()
    anchor = "    // Put the raw message into a string"
    path.write_text(replace(text, anchor, """    unsigned lines = 0;
    for (int i = 0; i < length; i++) {
        if (i >= 16384) return RTSP_ERROR_MALFORMED;
        if (rtspMessage[i] == '\\n') {
            if (++lines > 130) return RTSP_ERROR_MALFORMED;
            if (i > 0 && (rtspMessage[i - 1] == '\\n' ||
                (i > 1 && rtspMessage[i - 1] == '\\r' && rtspMessage[i - 2] == '\\n'))) break;
        }
    }
""" + anchor))


def prepare(platform, root):
    patch = HERE / (platform + ".patch")
    subprocess.run(["git", "apply", "--check", str(patch)], cwd=root, check=True)
    harden_native(root / NATIVE[platform], platform)
    subprocess.run(["git", "apply", str(patch)], cwd=root, check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("platform", choices=NATIVE)
    parser.add_argument("checkout", type=Path)
    args = parser.parse_args()
    prepare(args.platform, args.checkout.resolve())
