"""Bind pinned PiuLink client sources to the portable Rust stream core."""
import argparse
from pathlib import Path
import shutil


def once(text, old, new):
    if text.count(old) != 1:
        raise ValueError(f"Unsupported client source: {old[:65]}")
    return text.replace(old, new)


def readiness(text):
    text = once(text, "if (queue->pendingFecBlockList.count < neededPackets) {", """uint8_t readiness = pl_block_readiness(totalPackets, neededPackets,
        queue->pendingFecBlockList.count, queue->missingPackets, queue->reportedLostFrame, queue->receivedOosData);
    if (readiness != 1) {""")
    return once(text, "if (queue->missingPackets > totalPackets - neededPackets) {", "if (readiness == 2) {")


def frame_header(text):
    return once(text, "typedef struct _RTP_VIDEO_QUEUE {", """typedef struct {
    RTPV_QUEUE_LIST blocks[4];
    uint32_t number;
    uint8_t count, last;
} PlFrame;
typedef void (*PlConsume)(PRTPV_QUEUE_LIST, bool);
void pl_frame_discard(PlFrame *, PlConsume);
int32_t pl_frame_stage(PlFrame *, uint32_t, uint8_t, uint8_t, PRTPV_QUEUE_LIST, uint32_t, PlConsume);

typedef struct _RTP_VIDEO_QUEUE {
    PlFrame rustFrame;""")


def stage_frame(text):
    text = once(text, "void RtpvCleanupQueue", "static void consumeBlock(PRTPV_QUEUE_LIST, bool);\n\nvoid RtpvCleanupQueue")
    discard = "purgeListEntries(&queue->completedFecBlockList);"
    if text.count(discard) != 4:
        raise ValueError("Unknown frame discard paths")
    text = text.replace(discard, "pl_frame_discard(&queue->rustFrame, consumeBlock);")
    text = once(text, "static void submitCompletedFrame(PRTP_VIDEO_QUEUE queue) {",
                """static void consumeBlock(PRTPV_QUEUE_LIST list, bool deliver) {
    if (!deliver) {
        purgeListEntries(list);
        return;
    }""")
    begin = text.index("static void consumeBlock(PRTPV_QUEUE_LIST list")
    end = text.index("\n}", begin)
    body = text[begin:end].replace("queue->completedFecBlockList.", "list->").replace("&queue->completedFecBlockList", "list")
    text = text[:begin] + body + text[end:]
    text = once(text, "if (queue->multiFecCurrentBlockNumber < queue->multiFecLastBlockNumber) {\n                // Move on",
                """int action = pl_frame_stage(&queue->rustFrame, queue->currentFrameNumber,
                    queue->multiFecCurrentBlockNumber, queue->multiFecLastBlockNumber,
                    &queue->completedFecBlockList, queue->bufferDataPackets, consumeBlock);
            if (action == 0) {
                // Move on""")
    return once(text, "submitCompletedFrame(queue);",
                """if (action < 0 && !queue->reportedLostFrame) {
                    notifyFrameLost(queue->currentFrameNumber, false);
                }""")


def video(text):
    text = once(text, "    reed_solomon_init();", "")
    text = once(text, "    reed_solomon* rs = NULL;", "")
    start = text.index("    rs = reed_solomon_new(")
    end = text.index("    memset(marks,", start)
    text = text[:start] + text[end:]
    text = once(text, "    reed_solomon_release(rs);", "")
    for name in ("reed_solomon_decode", "reed_solomon_reconstruct"):
        call = f"{name}(rs, packets, marks, totalPackets, receiveSize)"
        if call in text:
            text = once(text, call, "(pl_video_recover(packets, marks, queue->bufferDataPackets, queue->bufferParityPackets, receiveSize) < 0 ? -1 : 0)")
            break
    else:
        raise ValueError("Unknown video recovery call")
    return '#include "piulink_portable.h"\n' + stage_frame(readiness(text))


def audio(text):
    start = text.index("    reed_solomon_init();")
    end_marker = "    memcpy(queue->rs->p, parity, sizeof(parity));"
    if end_marker not in text:
        end_marker = "    memcpy(queue->rs->parity, parity, sizeof(parity));"
    end = text.index(end_marker, start) + len(end_marker)
    if "0x77, 0x40, 0x38, 0x0e, 0xc7, 0xa7, 0x0d, 0x6c" not in text[start:end]:
        raise ValueError("Unsupported audio parity coefficients")
    text = text[:start] + text[end:]
    text = once(text, "    reed_solomon_release(queue->rs);", "")
    for name in ("reed_solomon_decode", "reed_solomon_reconstruct"):
        call = f"{name}(queue->rs, shards, block->marks, RTPA_TOTAL_SHARDS, block->blockSize)"
        if call in text:
            text = once(text, call, "(pl_audio_recover(shards, block->marks, block->blockSize) < 0 ? -1 : 0)")
            return '#include "piulink_portable.h"\n' + text
    raise ValueError("Unknown audio recovery call")


def parser(text):
    start = text.index("// Check if String s begins")
    end = text.index("// Create new RTSP message struct", start)
    text = text[:start] + """#include "piulink_rtsp.h"
int parseRtspMessage(PRTSP_MESSAGE msg, char *data, int len) {
    return pl_rtsp_parse(msg, data, len, malloc, free);
}
""" + text[end:]
    start = text.index("bool appendString(")
    end = text.index("// Free everything", start)
    return text[:start] + """char *serializeRtspMessage(PRTSP_MESSAGE msg, int *len) {
    return pl_rtsp_serialize(msg, len, malloc);
}
""" + text[end:]


def integrate(src, platform):
    here = Path(__file__).resolve().parent
    replacements = {"RtpVideoQueue.h": frame_header, "RtpVideoQueue.c": video, "RtpAudioQueue.c": audio, "ByteBuffer.c": lambda _: '#include "ByteBuffer.h"\n'}
    # This fork marks duplicate RTSP fields and compares names case-insensitively.
    if platform != "harmony":
        replacements["RtspParser.c"] = parser
    updates = []
    for name, transform in replacements.items():
        path = src / name
        original = path.read_text()
        converted = transform(original)
        if platform == "ios":
            # tvOS shares the Xcode static-library target with iOS.
            fallback = path.with_name(path.name + ".tvos.inc")
            updates.append((fallback, original))
            converted = (f'#include <TargetConditionals.h>\n#if TARGET_OS_TV\n#include "{fallback.name}"\n#else\n'
                         + converted + "\n#endif\n")
        updates.append((path, converted))
    for path, value in updates:
        path.write_text(value)
    shutil.copy2(here / "piulink_portable.h", src)
    if platform != "harmony":
        shutil.copy2(here.parent / "wire/rtsp_adapter.h", src / "piulink_rtsp.h")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("platform", choices=["ios", "macos", "harmony", "windows", "linux"])
    p.add_argument("native", type=Path)
    args = p.parse_args()
    integrate(args.native, args.platform)
