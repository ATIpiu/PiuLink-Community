package com.limelight.piulink.ssh;

import org.junit.Test;
import static org.junit.Assert.assertEquals;

public class TransferActivityTest {
    @Test public void sanitizesRemoteFileNames() {
        assertEquals("a_b_c_.txt", TransferActivity.safeName("a/b:c?.txt"));
        assertEquals("shared-file", TransferActivity.safeName(".."));
        assertEquals("shared-file", TransferActivity.safeName("\n"));
    }
}
