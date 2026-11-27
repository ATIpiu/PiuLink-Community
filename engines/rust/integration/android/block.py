"""Use Rust to admit shards and stage recovered data in RTP sequence order."""
from pathlib import Path
import shutil


def integrate_block(native: Path, wire: Path, code: str) -> str:
    shutil.copy2(wire / "block.h", native / "piulink_block.h")
    header = native / "RtpVideoQueue.h"
    definition = header.read_text()
    if definition.count("bool useFastQueuePath;") != 1:
        raise RuntimeError("Unexpected queue state layout")
    header.write_text('#include "piulink_block.h"\n' +
                      definition.replace("bool useFastQueuePath;", "PlVideoBlock rustBlock;"))
    start = code.index("    PRTPV_QUEUE_ENTRY entry;", code.index("static bool queuePacket("))
    end = code.index("    newEntry->packet = packet;", start)
    code = code[:start] + """    uint8_t admission = pl_block_accept(&queue->rustBlock, packet->sequenceNumber,
            &queue->nextContiguousSequenceNumber, queue->bufferDataPackets,
            queue->bufferDataPackets + queue->bufferParityPackets, newEntry);
    if (!admission) return false;
    bool outOfSequence = admission == 2;

""" + code[end:]
    start = code.index("    unsigned int nextSeqNum", code.index("static void stageCompleteFecBlock"))
    end = code.index("\n}", start)
    code = code[:start] + """    // Keep C allocation ownership while Rust indexes accepted and recovered shards.
    for (uint32_t i = 0; i < queue->bufferDataPackets; i++) {
        PRTPV_QUEUE_ENTRY entry = pl_block_entry(&queue->rustBlock, i);
        if (!entry) continue; // Reconstruction can discard a corrupt shard.
        removeEntryFromList(&queue->pendingFecBlockList, entry);
        entry->receiveTimeMs = queue->bufferFirstRecvTimeMs;
        insertEntryIntoList(&queue->completedFecBlockList, entry);
    }
    purgeListEntries(&queue->pendingFecBlockList);""" + code[end:]
    anchor = "queue->useFastQueuePath = true;"
    if code.count(anchor) != 1:
        raise RuntimeError("Unexpected block lifecycle")
    return code.replace(anchor, "pl_block_reset(&queue->rustBlock, queue->bufferLowestSequenceNumber);")
