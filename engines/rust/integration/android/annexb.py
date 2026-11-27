"""Route Moonlight AVC/HEVC Annex B parsing through the Rust packet parser."""
def integrate_nal(native):
    source = native / "VideoDepacketizer.c"
    code = source.read_text()
    def section(start, end, new):
        nonlocal code
        if code.count(start) != 1 or code.count(end) != 1:
            raise RuntimeError("Unexpected Moonlight depacketizer: " + start)
        a = code.index(start)
        code = code[:a] + new + code[code.index(end, a):]
    section("// NB: This function also ensures", "void validateDecodeUnitForPlayback", """
typedef struct { uint32_t offset, end; bool reference; } PlNalFragment;
enum { PL_OTHER, PL_SPS, PL_PPS, PL_VPS, PL_AUD, PL_SEI, PL_REFERENCE };
uint8_t pl_nal_kind(const char*, uint32_t, bool);
uint32_t pl_nal_leading(const char*, uint32_t, bool);
uint32_t pl_nal_next(const char*, uint32_t);
bool pl_nal_fragment(const char*, uint32_t, bool, PlNalFragment*);
static bool isHevc(void) { return (NegotiatedVideoFormat & VIDEO_FORMAT_MASK_H265) != 0; }

""")
    section("static bool isSeqReferenceFrameStart", "// Reassemble the frame with", "")
    section("static int getBufferFlags", "// As an optimization", """static int getBufferFlags(char* data, int length) {
    if (!(NegotiatedVideoFormat & (VIDEO_FORMAT_MASK_H264 | VIDEO_FORMAT_MASK_H265)))
        return BUFFER_TYPE_PICDATA;
    switch (pl_nal_kind(data, length, isHevc())) {
        case PL_SPS: return BUFFER_TYPE_SPS;
        case PL_PPS: return BUFFER_TYPE_PPS;
        case PL_VPS: return BUFFER_TYPE_VPS;
        default: return BUFFER_TYPE_PICDATA;
    }
}

""")
    section("static void processAvcHevcRtpPayloadSlow", "// Dumps the decode unit queue", """static void processAvcHevcRtpPayloadSlow(PBUFFER_DESC pos, PLENTRY_INTERNAL* existingEntry) {
    PlNalFragment fragment;
    while (pl_nal_fragment(pos->data + pos->offset, pos->length, isHevc(), &fragment)) {
        int start = pos->offset + fragment.offset;
#ifdef FORCE_3_BYTE_START_SEQUENCES
        ++start;
#endif
        if (fragment.reference) {
            waitingForIdrFrame = waitingForRefInvalFrame = waitingForNextSuccessfulFrame = false;
            frameType = FRAME_TYPE_IDR;
            if (nextFrameNumber == 1)
                Limelog("Rust Annex B first reference slice: %s\\n", isHevc() ? "HEVC" : "AVC");
        }
        queueFragment(fragment.reference ? existingEntry : NULL, pos->data, start,
                      pos->offset + fragment.end - start);
        pos->offset += fragment.end;
        pos->length -= fragment.end;
    }
}

""")
    section("            // The Annex B NALU start prefix", "        }\n    }\n    else {\n        // There is no frame header", """            unsigned int skip = pl_nal_leading(currentPos.data + currentPos.offset, currentPos.length, isHevc());
            currentPos.offset += skip;
            currentPos.length -= skip;
""")
    code = code.replace("firstPacket && isIdrFrameStart(&currentPos)",
                        "firstPacket && pl_nal_kind(currentPos.data + currentPos.offset, currentPos.length, isHevc()) == (isHevc() ? PL_VPS : PL_SPS)")
    code = code.replace("firstPacket && isPictureParameterSetNal(&currentPos)",
                        "firstPacket && pl_nal_kind(currentPos.data + currentPos.offset, currentPos.length, isHevc()) == PL_PPS")
    code = code.replace("skipToNextNal(&currentPos);", """unsigned int skip = pl_nal_next(currentPos.data + currentPos.offset, currentPos.length);
                currentPos.offset += skip;
                currentPos.length -= skip;""")
    # The control callbacks still run on Moonlight's receive thread.
    anchor = "        // Parse the frame type from the header"
    assert code.count(anchor) == 1
    code = code.replace(anchor, """        PlFrameHeader header;
        if (!pl_read_header((const uint8_t*)currentPos.data + currentPos.offset, currentPos.length,
                            AppVersionQuad, &header)) {
            decodingFrame = false;
            nextFrameNumber = frameIndex + 1;
            dropFrameState();
            if (waitingForIdrFrame) LiRequestIdrFrame();
            else connectionDetectedFrameLoss(startFrameNumber, frameIndex);
            return;
        }""")
    code = code.replace("currentPos.data[currentPos.offset + 3]", "header.kind")
    section("        // Sunshine can provide host processing latency", "        // We only parse H.264 and HEVC at the NALU level", """        if (IS_SUNSHINE() && currentPos.length >= 3) frameHostProcessingLatency = header.latency;
        if (!(NegotiatedVideoFormat & (VIDEO_FORMAT_MASK_H264 | VIDEO_FORMAT_MASK_H265)) && currentPos.length >= 6)
            lastPacketPayloadLength = header.tail;
        frameHeaderSize = header.size;
        currentPos.offset += frameHeaderSize;
        currentPos.length -= frameHeaderSize;

""")
    source.write_text('#include "piulink_fec.h"\n' + code)
