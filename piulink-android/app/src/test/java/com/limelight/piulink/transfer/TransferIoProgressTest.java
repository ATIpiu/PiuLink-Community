package com.limelight.piulink.transfer;

import org.junit.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import static org.junit.Assert.*;

public class TransferIoProgressTest {
    @Test public void reportsActualBytesWrittenIncludingTheSingleByteFallback() throws Exception {
        byte[] payload = {1, 2, 3, 4, 5};
        ByteArrayInputStream source = new ByteArrayInputStream(payload) {
            boolean first = true;
            @Override public synchronized int read(byte[] target, int offset, int length) {
                if (first) { first = false; return 0; }
                return super.read(target, offset, Math.min(2, length));
            }
        };
        ByteArrayOutputStream saved = new ByteArrayOutputStream();
        List<Long> updates = new ArrayList<>();
        TransferIo.copyExactly(source, saved, payload.length, updates::add);
        assertArrayEquals(payload, saved.toByteArray());
        assertEquals(Arrays.asList(1L, 3L, 5L), updates);
    }

    @Test public void truncatedSourceNeverReportsTheDeclaredSize() throws Exception {
        List<Long> updates = new ArrayList<>();
        assertThrows(EOFException.class, () -> TransferIo.copyExactly(new ByteArrayInputStream(new byte[3]),
                new ByteArrayOutputStream(), 5, updates::add));
        assertEquals(Arrays.asList(3L), updates);
    }
}
