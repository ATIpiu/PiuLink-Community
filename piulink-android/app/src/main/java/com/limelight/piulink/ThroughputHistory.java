package com.limelight.piulink;

import java.util.ArrayDeque;
import java.util.Arrays;

/** Recent delivered video throughput, not an estimate of unused link capacity. */
final class ThroughputHistory {
    private static final long WINDOW_MS = 60_000;
    private final ArrayDeque<Sample> samples = new ArrayDeque<>();

    void add(long nowMs, int kbps) {
        prune(nowMs);
        if (kbps > 0) samples.addLast(new Sample(nowMs, kbps));
    }

    void prune(long nowMs) {
        while (!samples.isEmpty() && nowMs - samples.peekFirst().timeMs >= WINDOW_MS) {
            samples.removeFirst();
        }
    }

    int lowerQuartileKbps() {
        if (samples.isEmpty()) return 0;
        int[] rates = new int[samples.size()];
        int index = 0;
        for (Sample sample : samples) rates[index++] = sample.kbps;
        Arrays.sort(rates);
        return rates[(rates.length - 1) / 4];
    }

    int size() {
        return samples.size();
    }

    private static final class Sample {
        final long timeMs;
        final int kbps;

        Sample(long timeMs, int kbps) {
            this.timeMs = timeMs;
            this.kbps = kbps;
        }
    }
}
