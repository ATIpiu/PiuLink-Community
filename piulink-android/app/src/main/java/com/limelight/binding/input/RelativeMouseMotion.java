package com.limelight.binding.input;

/** Preserves subpixel movement when Android deltas are encoded as protocol shorts. */
public final class RelativeMouseMotion {
    private int deviceId = Integer.MIN_VALUE;
    private double remainderX;
    private double remainderY;
    private short x;
    private short y;

    public void update(int sourceDeviceId, float dx, float dy) {
        if (sourceDeviceId != deviceId) {
            reset();
            deviceId = sourceDeviceId;
        }
        double totalX = remainderX + (Float.isNaN(dx) || Float.isInfinite(dx) ? 0 : dx);
        double totalY = remainderY + (Float.isNaN(dy) || Float.isInfinite(dy) ? 0 : dy);
        // Keep only the fractional part, never queue oversized movement into later events.
        totalX = Math.max(Short.MIN_VALUE, Math.min(Short.MAX_VALUE, totalX));
        totalY = Math.max(Short.MIN_VALUE, Math.min(Short.MAX_VALUE, totalY));
        x = (short) totalX;
        y = (short) totalY;
        remainderX = totalX - x;
        remainderY = totalY - y;
    }

    public short getX() { return x; }
    public short getY() { return y; }

    public void reset() {
        deviceId = Integer.MIN_VALUE;
        remainderX = remainderY = 0;
        x = y = 0;
    }
}
