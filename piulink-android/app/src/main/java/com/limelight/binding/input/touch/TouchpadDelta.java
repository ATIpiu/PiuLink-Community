package com.limelight.binding.input.touch;

/** Converts one touch axis to protocol pixels without losing small movements between events. */
final class TouchpadDelta {
    private double remainder;

    short move(int distance, double scale) {
        if (distance == 0) return 0;
        double exact = distance * scale + remainder;
        long pixels = Math.round(exact);
        remainder = exact - pixels;
        return (short) Math.max(Short.MIN_VALUE, Math.min(Short.MAX_VALUE, pixels));
    }

    void reset() {
        remainder = 0;
    }
}
