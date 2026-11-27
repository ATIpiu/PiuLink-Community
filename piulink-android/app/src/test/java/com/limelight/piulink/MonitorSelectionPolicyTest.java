package com.limelight.piulink;
import org.junit.Test;
import static org.junit.Assert.*;
public class MonitorSelectionPolicyTest {
    @Test public void followsHostForUnsetOrInvalidSelection() {
        for (int value : new int[]{-5, -1, MonitorSelectionPolicy.MAX_MONITOR_COUNT, Integer.MAX_VALUE}) assertEquals(-1, MonitorSelectionPolicy.normalize(value));
    }
    @Test public void mapsEnabledSlotsToFunctionKeys() {
        for (int i = 0; i < MonitorSelectionPolicy.MAX_MONITOR_COUNT; i++) assertEquals(i, MonitorSelectionPolicy.functionKeyOffset(i));
    }
    @Test(expected = IllegalArgumentException.class) public void doesNotSendShortcutForDefault() {
        MonitorSelectionPolicy.functionKeyOffset(-1);
    }
}
