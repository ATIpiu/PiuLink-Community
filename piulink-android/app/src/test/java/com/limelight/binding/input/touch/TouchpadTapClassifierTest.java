package com.limelight.binding.input.touch;

import org.junit.Test;

import static org.junit.Assert.assertEquals;

public class TouchpadTapClassifierTest {
    private TouchpadTapClassifier.Button classify(int actionIndex,
                                                   int maximumPointerCount,
                                                   int pointerCountBeforeUp,
                                                   boolean moved,
                                                   long elapsedMs) {
        return TouchpadTapClassifier.classify(
                actionIndex,
                maximumPointerCount,
                pointerCountBeforeUp,
                false,
                moved,
                false,
                false,
                true,
                elapsedMs,
                400);
    }

    @Test
    public void secondFingerCanLiftFirstForRightClick() {
        assertEquals(TouchpadTapClassifier.Button.RIGHT, classify(1, 2, 2, false, 180));
    }

    @Test
    public void firstFingerCanLiftFirstForRightClick() {
        assertEquals(TouchpadTapClassifier.Button.RIGHT, classify(0, 2, 2, false, 220));
    }

    @Test
    public void finalFingerUpDoesNotSendDuplicateRightClick() {
        assertEquals(TouchpadTapClassifier.Button.NONE, classify(0, 2, 1, false, 260));
    }

    @Test
    public void movementCancelsRightClickAndAllowsScrolling() {
        assertEquals(TouchpadTapClassifier.Button.NONE, classify(0, 2, 2, true, 160));
    }

    @Test
    public void relaxedTimeoutStillHasAnUpperBound() {
        assertEquals(TouchpadTapClassifier.Button.RIGHT, classify(0, 2, 2, false, 400));
        assertEquals(TouchpadTapClassifier.Button.NONE, classify(0, 2, 2, false, 401));
    }

    @Test
    public void singleFingerTapRemainsLeftClick() {
        assertEquals(TouchpadTapClassifier.Button.LEFT, classify(0, 1, 1, false, 150));
    }
}
