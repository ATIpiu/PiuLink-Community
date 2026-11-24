package com.limelight.preferences;

import org.junit.Test;

import static org.junit.Assert.assertArrayEquals;

public class PreferenceConfigurationTest {
    @Test
    public void smartDisplayUsesLandscapePhysicalResolution() {
        assertArrayEquals(
                new int[] { 3096, 2064 },
                PreferenceConfiguration.calculateSmartDisplayResolution(2064, 3096));
    }

    @Test
    public void smartDisplayRoundsCodecDimensionsDownToEvenPixels() {
        assertArrayEquals(
                new int[] { 2532, 1170 },
                PreferenceConfiguration.calculateSmartDisplayResolution(1171, 2533));
    }
}
