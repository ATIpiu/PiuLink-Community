package com.limelight.piulink;

import org.junit.Test;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

public class AutoCodecPolicyTest {
    @Test
    public void selectsEfficiencyOnlyForRemoteConstrainedOrHighResolutionSessions() {
        assertFalse(AutoCodecPolicy.preferAv1("192.168.1.20", 1920, 1080, false));
        assertTrue(AutoCodecPolicy.preferAv1("100.100.20.30", 1920, 1080, false));
        assertTrue(AutoCodecPolicy.preferAv1("fd7a:115c:a1e0::20", 1920, 1080, false));
        assertTrue(AutoCodecPolicy.preferAv1("gaming-pc.tail123.ts.net", 1920, 1080, false));
        assertTrue(AutoCodecPolicy.preferAv1("192.168.1.20", 2560, 1440, false));
        assertTrue(AutoCodecPolicy.preferAv1("192.168.1.20", 1920, 1080, true));
    }
}
