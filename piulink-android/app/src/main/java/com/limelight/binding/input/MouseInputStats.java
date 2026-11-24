package com.limelight.binding.input;

import java.util.Locale;

/** Timing only: no coordinates, buttons or typed content are retained. UI-thread confined. */
public final class MouseInputStats {
    private long first = -1, firstEvent = -1, last = -1, maxGap, maxDelivery;
    private int events, samples;

    public void record(long firstSampleTime, long eventTime, long deliveryTime, int historySize) {
        // A pause in movement is not an input stall. Start a new active burst after 250 ms.
        if (last >= 0 && eventTime - last > 250) reset();
        if (last >= 0 && eventTime >= last) maxGap = Math.max(maxGap, eventTime - last);
        if (first < 0) first = firstSampleTime;
        if (firstEvent < 0) firstEvent = eventTime;
        last = eventTime;
        events++;
        samples += historySize + 1;
        maxDelivery = Math.max(maxDelivery, Math.max(0, deliveryTime - firstSampleTime));
    }

    public String takeSummary(long now) {
        String result = "";
        long elapsed = last - first;
        if (events >= 2 && elapsed > 0 && last > firstEvent && now - last <= 500) {
            result = String.format(Locale.ROOT,
                    "Mouse input: %.0f events/s · %.0f samples/s · gap %d ms · dispatch %d ms",
                    (events - 1) * 1000.0 / (last - firstEvent), (samples - 1) * 1000.0 / elapsed,
                    maxGap, maxDelivery);
        }
        reset();
        return result;
    }

    public void reset() {
        first = firstEvent = last = -1;
        maxGap = maxDelivery = 0;
        events = samples = 0;
    }
}
