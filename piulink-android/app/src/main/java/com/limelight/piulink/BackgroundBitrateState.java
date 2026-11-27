package com.limelight.piulink;

/** Lifecycle state for lowering a live stream without losing its negotiated foreground rate. */
public final class BackgroundBitrateState {
    private final int backgroundCeilingKbps;
    private boolean active;
    private int foregroundKbps;

    public BackgroundBitrateState(int backgroundCeilingKbps) {
        if (backgroundCeilingKbps <= 0) throw new IllegalArgumentException("Background bitrate must be positive");
        this.backgroundCeilingKbps = backgroundCeilingKbps;
    }

    public synchronized int enter(int currentForegroundKbps) {
        if (!active) {
            if (currentForegroundKbps <= 0) throw new IllegalArgumentException("Foreground bitrate must be positive");
            foregroundKbps = currentForegroundKbps;
            active = true;
        }
        return Math.min(foregroundKbps, backgroundCeilingKbps);
    }

    public synchronized int targetForReconnectedStream() {
        return active ? Math.min(foregroundKbps, backgroundCeilingKbps) : 0;
    }

    public synchronized int leave() {
        if (!active) return 0;
        active = false;
        int restoreKbps = foregroundKbps;
        foregroundKbps = 0;
        return restoreKbps;
    }

    public synchronized boolean isActive() {
        return active;
    }
}
