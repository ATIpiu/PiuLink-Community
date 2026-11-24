package com.limelight.binding.input;

import org.junit.Test;
import static org.junit.Assert.*;

public class MouseInputStatsTest {
    @Test public void separatesBatchingFromSampleRateAndDeliveryLag() {
        MouseInputStats stats = new MouseInputStats();
        stats.record(0, 8, 10, 1);
        stats.record(16, 24, 30, 1);
        String result = stats.takeSummary(30);
        assertTrue(result.contains("63 events/s"));
        assertTrue(result.contains("125 samples/s"));
        assertTrue(result.contains("gap 16 ms"));
        assertTrue(result.contains("dispatch 14 ms"));
        assertEquals("", stats.takeSummary(31));
    }

    @Test public void idleTimeIsNotReportedAsMouseStall() {
        MouseInputStats stats = new MouseInputStats();
        stats.record(0, 0, 0, 0);
        stats.record(1000, 1000, 1000, 0);
        stats.record(1008, 1008, 1009, 0);
        assertTrue(stats.takeSummary(1010).contains("gap 8 ms"));
        stats.record(1020, 1020, 1020, 0);
        stats.record(1028, 1028, 1028, 0);
        assertEquals("", stats.takeSummary(2000));
    }
}
