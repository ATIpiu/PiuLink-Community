package com.limelight.piulink.ssh;

import java.util.ArrayDeque;
import java.util.Arrays;

/** One paste is admitted in full or rejected, never partially queued as a command. */
final class TerminalInputBuffer {
    static final int FRAME_BYTES = 16384;
    private static final int MAX_FRAMES = 16;
    private final ArrayDeque<byte[]> frames = new ArrayDeque<>();

    synchronized boolean offer(byte[] bytes, int offset, int length) {
        if (offset < 0 || length < 0 || offset > bytes.length - length)
            throw new IndexOutOfBoundsException();
        if (length > MAX_FRAMES * FRAME_BYTES) return false;
        int needed = (length + FRAME_BYTES - 1) / FRAME_BYTES;
        if (needed > MAX_FRAMES - frames.size()) return false;
        for (int start = offset; start < offset + length; start += FRAME_BYTES)
            frames.addLast(Arrays.copyOfRange(bytes, start, Math.min(start + FRAME_BYTES, offset + length)));
        return true;
    }
    synchronized byte[] poll() { return frames.pollFirst(); }
    synchronized boolean isEmpty() { return frames.isEmpty(); }
    synchronized void clear() { frames.clear(); }
}
