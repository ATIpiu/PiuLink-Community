"""Generate an isolated differential harness from the original Moonlight queue source."""
import argparse
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("source", type=Path)
p.add_argument("output", type=Path)
args = p.parse_args()
original = args.source.read_text()
def section(start, end):
    return original[original.index(start):original.index(end)]

preamble = """
#include <assert.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "block.h"
#define U16(x) ((uint16_t)(x))
#define isBefore16(x,y) (U16((x)-(y)) > 32767)
#define LC_ASSERT(x) assert(x)
#define Limelog(...) ((void)0)
#define PTS_DIVISOR 90
#define SPECULATIVE_RFI_COOLDOWN_PERIOD_MS 300000
typedef struct { uint16_t sequenceNumber; uint32_t timestamp; } RTP_PACKET, *PRTP_PACKET;
"""
header = args.source.with_suffix(".h").read_text()
types = header[header.index("typedef struct"):header.index("#define RTPF_RET_QUEUED")]
main = """
int main(void) {
    const uint16_t bases[] = {0, 1, 65000, 65534, 65535};
    uint32_t rng = 1234, checks = 0;
    for (unsigned trial=0; trial < 1000; ++trial) {
        RTP_VIDEO_QUEUE q = {0};
        uint16_t base = bases[trial % 5];
        q.bufferLowestSequenceNumber = q.nextContiguousSequenceNumber = base;
        q.useFastQueuePath = true;
        q.bufferFirstRecvTimeMs = 1;
        PlVideoBlock rust = {0};
        pl_block_reset(&rust, base);
        uint32_t next = base;
        unsigned data = 1 + trial % 121, total = data + 19;
        uintptr_t ids[140] = {0};
        for (unsigned step = 0; step < total * 4; ++step) {
            rng = rng * 1664525u + 1013904223u;
            unsigned idx = step < total * 3 ? (trial % 3 ? (rng >> 9) % total : step % total) : step - 3*total;
            uint16_t seq = (uint16_t)(base + idx);
            if (isBefore16(seq, next)) continue; // RtpvAddPacket ingress window
            PRTP_PACKET pkt = calloc(1, sizeof(RTP_PACKET) + sizeof(RTPV_QUEUE_ENTRY));
            PRTPV_QUEUE_ENTRY e = (void*)(pkt+1);
            pkt->sequenceNumber = seq;
            pkt->timestamp = (checks+1)*90;
            uint8_t result = pl_block_accept(&rust,seq,&next,data,total,e);
            bool accepted = queuePacket(&q,e,pkt,32,idx >= data,false);
            assert(accepted == (result != 0) && next == q.nextContiguousSequenceNumber);
            if (accepted) {
                assert((q.lastOosFramePresentationTimestamp == e->presentationTimeMs) == (result == 2));
                ids[idx] = (uintptr_t)e;
            } else free(pkt);
            ++checks;
        }
        stageCompleteFecBlock(&q);
        PRTPV_QUEUE_ENTRY e = q.completedFecBlockList.head;
        for (unsigned i=0; i<data; ++i) {
            assert(e && pl_block_entry(&rust,i) == e && ids[i] == (uintptr_t)e);
            e = e->next;
        }
        assert(!e);
        purgeListEntries(&q.completedFecBlockList);
    }
    printf("%u admission decisions and 1000 staged blocks matched upstream C\\n", checks);
}
"""
args.output.write_text(preamble + types +
                       section("static void purgeListEntries", "void RtpvCleanupQueue") +
                       section("static void insertEntryIntoList", "static void reportFinalFrameFecStatus") +
                       section("static bool queuePacket", "#define PACKET_RECOVERY_FAILURE") +
                       section("static void stageCompleteFecBlock", "static void submitCompletedFrame") + main)
