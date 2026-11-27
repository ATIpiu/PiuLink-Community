"""Replace Moonlight video recovery and cross-block staging in the Android build."""
from pathlib import Path


def integrate_frame(native: Path, code: str) -> str:
    header = native / "RtpVideoQueue.h"
    text = header.read_text()
    anchor = "typedef struct _RTP_VIDEO_QUEUE {"
    text = text.replace(anchor, """typedef struct {
    RTPV_QUEUE_LIST blocks[4];
    uint32_t number;
    uint8_t count, last;
} PlFrame;
typedef void (*PlConsume)(PRTPV_QUEUE_LIST, bool);
void pl_frame_discard(PlFrame *, PlConsume);
int32_t pl_frame_stage(PlFrame *, uint32_t, uint8_t, uint8_t, PRTPV_QUEUE_LIST, uint32_t, PlConsume);

""" + anchor + "\n    PlFrame rustFrame;")
    header.write_text(text)
    code = code.replace("    reed_solomon_init();","")
    code = code.replace("    reed_solomon* rs = NULL;", "")
    start = code.index("    rs = reed_solomon_new(")
    end = code.index("    memset(marks", start)
    code = code[:start] + code[end:]
    code = code.replace("    reed_solomon_release(rs);", "")
    old = "ret = reed_solomon_reconstruct(rs, packets, marks, totalPackets, receiveSize);"
    assert code.count(old) == 1
    code = code.replace(old, """int recovered = pl_video_recover(packets, marks, queue->bufferDataPackets,
                                     queue->bufferParityPackets, receiveSize);
    ret = recovered < 0 ? -1 : 0;
    if (recovered > 0 && queue->currentFrameNumber % 60 == 0)
        Limelog("Rust recovered frame=%u block=%u missing=%d\\n",
                queue->currentFrameNumber, queue->multiFecCurrentBlockNumber, recovered);""")
    code = code.replace("void RtpvCleanupQueue", "static void consumeBlock(PRTPV_QUEUE_LIST list, bool deliver);\n\nvoid RtpvCleanupQueue")
    code = code.replace("purgeListEntries(&queue->completedFecBlockList);",
                        "pl_frame_discard(&queue->rustFrame, consumeBlock);")
    start = code.index("static void submitCompletedFrame")
    end = code.index("uint32_t RtpvGetCurrentFrameNumber",start)
    code = code[:start] + """static void consumeBlock(PRTPV_QUEUE_LIST list, bool deliver) {
    if (!deliver) { purgeListEntries(list); return; }
    while (list->head) {
        PRTPV_QUEUE_ENTRY entry = list->head;
        removeEntryFromList(list, entry);
        queueRtpPacket(entry);
    }
}

""" + code[end:]
    start = code.index("            // If we're not yet at the last FEC block")
    end = code.index("\n        }", start)
    code = code[:start] + """            int action = pl_frame_stage(&queue->rustFrame, queue->currentFrameNumber,
                    queue->multiFecCurrentBlockNumber, queue->multiFecLastBlockNumber,
                    &queue->completedFecBlockList, queue->bufferDataPackets, consumeBlock);
            if (action == 0) {
                queue->multiFecCurrentBlockNumber++;
            } else {
                if (action < 0 && !queue->reportedLostFrame)
                    notifyFrameLost(queue->currentFrameNumber, false);
                if (action > 0 && queue->multiFecLastBlockNumber && queue->currentFrameNumber % 60 == 0)
                    Limelog("Rust delivered frame=%u blocks=%u\\n", queue->currentFrameNumber,
                            queue->multiFecLastBlockNumber + 1);
                queue->currentFrameNumber++;
                queue->multiFecCurrentBlockNumber = 0;
            }""" + code[end:]
    return code

def integrate_audio(native: Path):
    source = native / "RtpAudioQueue.c"
    source.write_bytes(Path(__file__).with_name("audio_queue.c").read_bytes())
    header = native / "RtpAudioQueue.h"
    code = header.read_text()
    start = code.index("typedef struct _RTPA_FEC_BLOCK {")
    end = code.index("} RTP_AUDIO_QUEUE, *PRTP_AUDIO_QUEUE;", start)
    header.write_text(code[:start] + "typedef struct _RTP_AUDIO_QUEUE {\n    void* rust;\n" + code[end:])

    stream = native / "AudioStream.c"
    code = stream.read_text()
    start = code.index("        uint32_t ivSeq =")
    end = code.index("        if (!PltDecryptMessage", start)
    stream.write_text('#include "piulink_fec.h"\n' + code[:start] +
                      "        pl_audio_iv(iv, avRiKeyId, rtp->sequenceNumber);\n\n" + code[end:])
