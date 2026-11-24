package com.limelight.piulink;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class VirtualResolutionPolicyTest {
    @Test
    public void rememberedModeWins() {
        assertEquals(2, VirtualResolutionPolicy.preferredIndex(
                new String[] {"1366x768", "1920x1080", "3840x2160"}, "3840x2160"));
    }

    @Test
    public void recommendedModeDoesNotDependOnHostOrdering() {
        assertEquals(2, VirtualResolutionPolicy.preferredIndex(
                new String[] {"1366x768", "3840x2160", "2560x1440", "1920x1080"}, ""));
    }

    @Test
    public void removedRememberedModeFallsBackSafely() {
        assertEquals(1, VirtualResolutionPolicy.preferredIndex(
                new String[] {"1366x768", "1920x1080"}, "3840x2160"));
    }
}
