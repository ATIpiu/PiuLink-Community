package com.limelight.binding.input;

import org.junit.Test;
import static org.junit.Assert.assertEquals;

public class RelativeMouseMotionTest {
    @Test public void slowMovementIsNotLost() {
        RelativeMouseMotion motion = new RelativeMouseMotion();
        int x = 0, y = 0;
        for (int i = 0; i < 100; i++) {
            motion.update(1, 0.25f, -0.5f);
            x += motion.getX();
            y += motion.getY();
        }
        assertEquals(25, x);
        assertEquals(-50, y);
    }

    @Test public void reversalCancelsFractionWithoutDrift() {
        RelativeMouseMotion motion = new RelativeMouseMotion();
        motion.update(1, 0.75f, -0.75f);
        motion.update(1, -0.75f, 0.75f);
        assertEquals(0, motion.getX());
        assertEquals(0, motion.getY());
        motion.update(1, 0.5f, -0.5f);
        assertEquals(0, motion.getX());
        assertEquals(0, motion.getY());
    }

    @Test public void captureAndDeviceChangesDiscardOldRemainders() {
        RelativeMouseMotion motion = new RelativeMouseMotion();
        motion.update(1, 0.75f, 0.75f);
        motion.update(2, 0.5f, 0.5f);
        assertEquals(0, motion.getX());
        motion.reset();
        motion.update(2, 0.5f, 0.5f);
        assertEquals(0, motion.getY());
    }

    @Test public void oversizedAndInvalidInputCannotWrapOrLeak() {
        RelativeMouseMotion motion = new RelativeMouseMotion();
        motion.update(1, Float.MAX_VALUE, -Float.MAX_VALUE);
        assertEquals(Short.MAX_VALUE, motion.getX());
        assertEquals(Short.MIN_VALUE, motion.getY());
        motion.update(1, Float.NaN, Float.POSITIVE_INFINITY);
        assertEquals(0, motion.getX());
        assertEquals(0, motion.getY());
    }
}
