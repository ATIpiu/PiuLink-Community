package com.limelight.piulink;

import java.util.List;

final class DesktopLayout {
    enum Direction { LEFT, RIGHT, UP, DOWN }
    static final class Bounds {
        final int x, y, width, height;
        Bounds(int x, int y, int width, int height) {
            this.x = x; this.y = y; this.width = width; this.height = height;
        }
    }

    static int neighbor(List<DisplayInventoryClient.Display> displays, int current, Direction direction) {
        if (current < 0 || current >= displays.size()) return -1;
        Bounds from = displays.get(current).bounds;
        if (from == null) return -1;
        boolean horizontal = direction == Direction.LEFT || direction == Direction.RIGHT;
        int sign = direction == Direction.LEFT || direction == Direction.UP ? -1 : 1;
        int best = -1;
        boolean bestOverlaps = false;
        double bestDistance = Double.POSITIVE_INFINITY;
        for (int i = 0; i < displays.size(); i++) {
            Bounds to = displays.get(i).bounds;
            if (!displays.get(i).isActive() || i == current) continue;
            double dx = (double)to.x - from.x + (to.width - from.width) * 0.5;
            double dy = (double)to.y - from.y + (to.height - from.height) * 0.5;
            double along = sign * (horizontal ? dx : dy);
            double across = Math.abs(horizontal ? dy : dx);
            if (along <= 0) continue;
            double overlap = (horizontal ? (double)from.height + to.height : (double)from.width + to.width) * 0.5 - across;
            boolean overlaps = overlap > 0;
            if (!overlaps && across > along) continue;
            double gap = Math.max(0, along - (horizontal ? (double)from.width + to.width : (double)from.height + to.height) * 0.5);
            double distance = Math.hypot(gap, Math.max(0, -overlap)) + Math.hypot(along, across) * 0.000001;
            if ((overlaps && !bestOverlaps) || (overlaps == bestOverlaps && distance < bestDistance)) {
                best = i; bestDistance = distance; bestOverlaps = overlaps;
            }
        }
        return best;
    }

    static int onlyOtherActive(List<DisplayInventoryClient.Display> screens, int current) {
        if (current < 0 || current >= screens.size() || !screens.get(current).isActive()) return -1;
        int other = -1;
        for (int i = 0; i < screens.size(); i++) {
            if (i == current || !screens.get(i).isActive()) continue;
            if (other >= 0) return -1;
            other = i;
        }
        return other;
    }

    private DesktopLayout() {}
}
