package com.limelight.binding.input.touch;

import org.junit.Test;
import static org.junit.Assert.assertEquals;

public class TouchpadDeltaTest {
    @Test public void scaleChangesDistanceInBothDirections() {
        for (int percent : new int[]{50, 75, 100, 150, 200, 300, 400}) {
            TouchpadDelta axis = new TouchpadDelta();
            assertEquals(percent, axis.move(100, percent / 100.0));
            assertEquals(-percent, axis.move(-100, percent / 100.0));
        }
    }

    @Test public void slowFineMovementSurvivesCoalescingAndReversal() {
        TouchpadDelta axis = new TouchpadDelta();
        int sum = 0;
        for (int i = 0; i < 40; i++) sum += axis.move(1, 0.125);
        assertEquals(5, sum);
        for (int i = 0; i < 40; i++) sum += axis.move(-1, 0.125);
        assertEquals(0, sum);
        assertEquals(0, axis.move(1, 0.25));
        axis.reset();
        assertEquals(0, axis.move(1, 0.25));
        assertEquals(0, axis.move(0, 4));
    }

    @Test public void aLargeMotionCannotWrapAround() {
        TouchpadDelta axis = new TouchpadDelta();
        assertEquals(Short.MAX_VALUE, axis.move(20000, 4));
        assertEquals(Short.MIN_VALUE, axis.move(-20000, 4));
    }
}
