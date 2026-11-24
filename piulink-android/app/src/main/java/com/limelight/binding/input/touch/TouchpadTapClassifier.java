package com.limelight.binding.input.touch;

/**
 * Determines which mouse button a completed trackpad tap represents. Keeping
 * this independent from Android input classes makes finger-order edge cases
 * deterministic and unit testable.
 */
public final class TouchpadTapClassifier {
    public enum Button {
        NONE,
        LEFT,
        RIGHT
    }

    private TouchpadTapClassifier() {
    }

    public static Button classify(int actionIndex,
                                  int maximumPointerCount,
                                  int pointerCountBeforeUp,
                                  boolean gestureWasCancelled,
                                  boolean gestureMoved,
                                  boolean gestureDragged,
                                  boolean gestureScrolled,
                                  boolean withinTapBounds,
                                  long elapsedMs,
                                  long tapTimeoutMs) {
        if (gestureWasCancelled || gestureMoved || gestureDragged || gestureScrolled ||
                !withinTapBounds || elapsedMs > tapTimeoutMs) {
            return Button.NONE;
        }

        if (maximumPointerCount == 2) {
            // Commit on whichever finger lifts first. Requiring the original
            // pointer at index 1 to lift first made two-finger clicks unreliable.
            return pointerCountBeforeUp == 2 ? Button.RIGHT : Button.NONE;
        }

        if (maximumPointerCount == 1 && actionIndex == 0) {
            return Button.LEFT;
        }

        return Button.NONE;
    }
}
