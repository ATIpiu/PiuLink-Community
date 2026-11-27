package com.limelight.piulink.ssh;

import org.junit.Test;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import static org.junit.Assert.*;

public class TerminalInputBufferTest {
    @Test public void longUnicodePasteIsDeliveredInOrderWithoutChangingBytes() throws Exception {
        StringBuilder value = new StringBuilder();
        for (int i = 0; i < 30000; i++) value.append("中文");
        byte[] bytes = value.toString().getBytes(StandardCharsets.UTF_8);
        TerminalInputBuffer buffer = new TerminalInputBuffer();
        assertTrue(buffer.offer(bytes, 0, bytes.length));
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] frame;
        while ((frame = buffer.poll()) != null) {
            assertTrue(frame.length <= 16384);
            output.write(frame);
        }
        assertArrayEquals(bytes, output.toByteArray());
    }
    @Test public void insufficientSpaceRejectsEntirePaste() {
        TerminalInputBuffer buffer = new TerminalInputBuffer();
        byte[] queued = new byte[15 * 16384]; Arrays.fill(queued, (byte) 1);
        assertTrue(buffer.offer(queued, 0, queued.length));
        assertFalse(buffer.offer(new byte[16385], 0, 16385));
        int frames = 0;
        byte[] frame;
        while ((frame = buffer.poll()) != null) { frames++; assertEquals(1, frame[0]); }
        assertEquals(15, frames);
    }
    @Test public void cancellationClearsAndOversizedPasteDoesNotOccupyQueue() {
        TerminalInputBuffer buffer = new TerminalInputBuffer();
        assertFalse(buffer.offer(new byte[262145], 0, 262145));
        assertTrue(buffer.isEmpty());
        assertTrue(buffer.offer(new byte[] {0, 1, 2, 3}, 1, 2));
        assertArrayEquals(new byte[] {1, 2}, buffer.poll());
        buffer.offer(new byte[5], 0, 5); buffer.clear();
        assertTrue(buffer.isEmpty());
    }
}
