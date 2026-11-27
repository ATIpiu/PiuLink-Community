package com.limelight.piulink;

public final class MonitorSelectionPolicy {
    public static final int MAX_MONITOR_COUNT = 12;

    private MonitorSelectionPolicy() { }
    public static int normalize(int index) { return index >= 0 && index < MAX_MONITOR_COUNT ? index : -1; }
    public static int functionKeyOffset(int index) {
        if (normalize(index) < 0) throw new IllegalArgumentException("Invalid monitor selection");
        return index;
    }
}
