package com.limelight.piulink;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public class BackgroundBitrateStateTest {
    @Test public void restoresTheOriginalForegroundRateAfterRepeatedTransitions() {
        BackgroundBitrateState state = new BackgroundBitrateState(750);
        assertEquals(750, state.enter(40_000));
        assertEquals(750, state.enter(10_000));
        assertTrue(state.isActive());
        assertEquals(40_000, state.leave());
        assertFalse(state.isActive());
        assertEquals(0, state.leave());
    }

    @Test public void neverRaisesAnAlreadyLowForegroundRate() {
        BackgroundBitrateState state = new BackgroundBitrateState(750);
        assertEquals(500, state.enter(500));
        assertEquals(500, state.targetForReconnectedStream());
        assertEquals(500, state.leave());
    }

    @Test public void reappliesLowRateWhenTransportReconnectsInBackground() {
        BackgroundBitrateState state = new BackgroundBitrateState(750);
        assertEquals(0, state.targetForReconnectedStream());
        state.enter(25_000);
        assertEquals(750, state.targetForReconnectedStream());
    }
}
