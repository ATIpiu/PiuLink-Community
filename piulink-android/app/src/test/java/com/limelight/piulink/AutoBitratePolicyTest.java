package com.limelight.piulink;

import org.junit.Test;

import static org.junit.Assert.assertEquals;

public class AutoBitratePolicyTest {
    @Test public void frameLossHoldsQualityThroughRecoveryOnVpnAndLan() {
        for (boolean vpn : new boolean[] {true, false}) {
            AutoBitratePolicy.Feedback f = new AutoBitratePolicy.Feedback(vpn, 4000, 14000, true);
            for (int i = 0; i < 30; i++)
                assertEquals(0, f.nextEncoderTarget(2708, 800, 16, 60, 180, i * 1000L));
            for (int i = 30; i < 42; i++)
                assertEquals(0, f.nextEncoderTarget(2708, 2600, 0, 60, 30, i * 1000L));
            org.junit.Assert.assertTrue(f.nextEncoderTarget(2708, 2600, 0, 60, 30, 42000) > 2708);
        }
    }
    @Test public void physicalInviteUsesItsOwnBudgetWhileVpnIsActive() {
        assertEquals(14000, AutoBitratePolicy.handshakeBudget(14000, AutoBitrateManager.usesVpnTransport(true, true)));
        assertEquals(4000, AutoBitratePolicy.handshakeBudget(14000, AutoBitrateManager.usesVpnTransport(true, false)));
        assertEquals(14000, AutoBitratePolicy.handshakeBudget(14000, AutoBitrateManager.usesVpnTransport(false, false)));
    }

    @Test public void onlyVpnInvitesSeparateHandshakeFromBudget() {
        assertEquals(14000, AutoBitratePolicy.handshakeBudget(14000, false));
        assertEquals(4000, AutoBitratePolicy.handshakeBudget(14000, true));
        assertEquals(3000, AutoBitratePolicy.handshakeBudget(6000, true));
        assertEquals(3000, AutoBitratePolicy.handshakeBudget(3000, true));
    }

    @Test public void healthyVpnInviteProbesUpToItsOriginalVideoShare() {
        AutoBitratePolicy.Feedback f = new AutoBitratePolicy.Feedback(true, 4000, 14000);
        int applied = 3000;
        for (int sec = 0; sec < 30; sec++) {
            int next = f.nextEncoderTarget(applied, applied, 0, 60, 25, sec * 1000L);
            if (next > 0) applied = next;
            if (sec == 3) assertEquals(4000, applied);
        }
        assertEquals(10500, applied);
    }

    @Test public void sparseLostFramesCutVpnRateAndStopFastProbing() {
        AutoBitratePolicy.Feedback f = new AutoBitratePolicy.Feedback(true, 4000, 14000);
        assertEquals(0, f.nextEncoderTarget(3000, 200, 0, 3, 50, 0));
        int reduced = f.nextEncoderTarget(3000, 200, 3, 7, 180, 1000);
        assertEquals(1650, reduced);
        for (int sec = 2; sec < 11; sec++)
            assertEquals(0, f.nextEncoderTarget(reduced, reduced, 0, 60, 50, sec * 1000L));
        assertEquals(1925, f.nextEncoderTarget(reduced, reduced, 0, 60, 50, 11000));
    }

    @Test public void slowIdleOrInterruptedSamplesDoNotTriggerFastProbes() {
        for (int rtt : new int[]{0, 220}) {
            AutoBitratePolicy.Feedback f = new AutoBitratePolicy.Feedback(true, 4000, 14000);
            for (int sec = 0; sec < 9; sec++)
                assertEquals(0, f.nextEncoderTarget(3000, 3000, 0, 60, rtt, sec * 1000L));
        }
        AutoBitratePolicy.Feedback f = new AutoBitratePolicy.Feedback(true, 4000, 14000);
        for (int sec = 0; sec < 20; sec++)
            assertEquals(0, f.nextEncoderTarget(3000, 90, 0, 60, 20, sec * 1000L));
        for (int sec : new int[]{20, 21, 22, 30, 31, 32})
            assertEquals(0, f.nextEncoderTarget(3000, 3000, 0, 60, 20, sec * 1000L));
        assertEquals(4000, f.nextEncoderTarget(3000, 3000, 0, 60, 20, 33000));
    }

    @Test public void tvKeepsFrameRateWithinSupportedOutputModes() {
        DisplayInventoryClient.Mode fullHd = new DisplayInventoryClient.Mode(1920, 1080, 60);
        DisplayInventoryClient.Mode source = new DisplayInventoryClient.Mode(3840, 2160, 60);
        DisplayInventoryClient.Mode fitted = AutoBitratePolicy.fitTvOutput(source, java.util.Arrays.asList(
                new DisplayInventoryClient.Mode(3840, 2160, 30), fullHd));
        assertEquals(1920, fitted.width);
        assertEquals(1080, fitted.height);
        assertEquals(60, fitted.fps);
        assertEquals(source, AutoBitratePolicy.fitTvOutput(source, java.util.Arrays.asList(fullHd, source)));
    }

    @Test public void tvPreservesPortraitAspectAndRemoteCaps() {
        DisplayInventoryClient.Mode fullHd = new DisplayInventoryClient.Mode(1920, 1080, 60);
        DisplayInventoryClient.Mode portrait = new DisplayInventoryClient.Mode(1080, 1920, 60);
        DisplayInventoryClient.Mode fitted = AutoBitratePolicy.fitTvOutput(portrait, java.util.Collections.singletonList(fullHd));
        assertEquals(606, fitted.width);
        assertEquals(1080, fitted.height);
        DisplayInventoryClient.Mode remote = new DisplayInventoryClient.Mode(1280, 720, 30);
        assertEquals(remote, AutoBitratePolicy.fitTvOutput(remote, java.util.Collections.singletonList(fullHd)));
    }

    @Test public void directLanStartsFromResolutionAndCurrentLinkEstimate() {
        assertEquals(30_000, AutoBitratePolicy.startupBitrate(
                "local", 1920, 1080, 60, 100_000, false, "balanced"));
        assertEquals(14_000, AutoBitratePolicy.startupBitrate(
                "local", 3840, 2160, 60, 20_000, false, "balanced"));
        assertEquals(60_000, AutoBitratePolicy.startupBitrate(
                "local", 3840, 2160, 60, 0, false, "balanced"));
    }

    @Test public void vpnDoesNotTreatBearerBandwidthAsPathCapacity() {
        assertEquals(8_000, AutoBitratePolicy.startupBitrate(
                "private", 3840, 2160, 60, 1_000_000, false, "balanced"));
        assertEquals(14_000, AutoBitratePolicy.startupBitrate(
                "private", 3840, 2160, 60, 0, false, "quality"));
    }

    @Test public void meteredSavingCapsStartup() {
        assertEquals(10_000, AutoBitratePolicy.startupBitrate(
                "metered", 1920, 1080, 60, 100_000, true, "quality"));
        assertEquals(6_000, AutoBitratePolicy.startupBitrate(
                "metered", 1920, 1080, 60, 100_000, false, "stable"));
    }

    @Test public void remotePhysicalModeFitsTheSourceInEitherOrientation() {
        assertMode(1920, 1080, 30, "private", 3840, 2160, true, "balanced", false);
        assertMode(1080, 1728, 30, "remote", 1600, 2560, true, "balanced", false);
        assertMode(2560, 1080, 60, "private", 5120, 2160, true, "quality", false);
        assertMode(1280, 720, 30, "metered", 3840, 2160, true, "quality", true);
        assertMode(800, 600, 30, "private", 800, 600, true, "stable", false);
        assertMode(3840, 2160, 60, "local", 3840, 2160, true, "balanced", false);
        assertMode(3840, 2160, 60, "private", 3840, 2160, false, "balanced", false);
    }

    private static void assertMode(int width, int height, int fps, String route, int sourceWidth,
                                   int sourceHeight, boolean auto, String mode, boolean save) {
        DisplayInventoryClient.Mode result = AutoBitratePolicy.physicalMode(route, sourceWidth, sourceHeight, auto, mode, save);
        assertEquals(width, result.width);
        assertEquals(height, result.height);
        assertEquals(fps, result.fps);
    }

    @Test public void screenshotLossReducesConfirmedEncoderRateAndThenRecoversOnlyWithDemand() {
        AutoBitratePolicy.Feedback feedback = new AutoBitratePolicy.Feedback();
        int applied = 6000;
        for (int second = 0; second < 20; second++) {
            int requested = feedback.nextEncoderTarget(applied, 1200, 11, 63, 301, second * 1000L);
            if (requested > 0) applied = requested;
        }
        assertEquals(750, applied);
        for (int second = 20; second < 80; second++)
            assertEquals(0, feedback.nextEncoderTarget(applied, 80, 0, 30, 301, second * 1000L));
        for (int second = 80; second < 140; second++) {
            int target = feedback.nextEncoderTarget(applied, applied, 0, 30, 50, second * 1000L);
            if (target > 0) applied = target;
        }
        org.junit.Assert.assertTrue(applied > 750 && applied <= 6000);
        assertEquals(0, feedback.nextEncoderTarget(0, 1200, 11, 63, 301, 200000));
    }

    @Test public void localTargetWinsEvenWhenTailscaleVpnIsActive() {
        assertEquals("local", AutoBitrateManager.classifyRoute(
                "192.168.1.8", true, false, true));
        assertEquals("private", AutoBitrateManager.classifyRoute(
                "100.70.102.126", true, false, true));
    }

    @Test public void privateAddressReachedThroughVpnUsesOverlayBudget() {
        String route = AutoBitrateManager.classifyRoute("192.168.8.2", true, false, false);
        assertEquals("private", route);
        assertEquals(8000, AutoBitratePolicy.startupBitrate(route, 3840, 2160, 60, 200000, false, "balanced"));
    }

    @Test public void vpnQueueReducesBeforeLossAndRecoversAfterDelayClears() {
        AutoBitratePolicy.Feedback feedback = new AutoBitratePolicy.Feedback(true);
        for (int i = 0; i < 3; i++) assertEquals(0, feedback.nextEncoderTarget(8000, 7500, 0, 60, 40, i * 1000));
        assertEquals(0, feedback.nextEncoderTarget(8000, 7500, 0, 60, 120, 3000));
        assertEquals(0, feedback.nextEncoderTarget(8000, 7500, 0, 60, 130, 4000));
        assertEquals(6400, feedback.nextEncoderTarget(8000, 7500, 0, 60, 125, 5000));
        for (int i = 6; i < 21; i++) assertEquals(0, feedback.nextEncoderTarget(6400, 6000, 0, 60, 127, i * 1000));
        for (int i = 21; i < 30; i++) assertEquals(0, feedback.nextEncoderTarget(6400, 6000, 0, 60, 40, i * 1000));
        org.junit.Assert.assertTrue(feedback.nextEncoderTarget(6400, 6000, 0, 60, 40, 30000) > 6400);
    }

    @Test public void quietDesktopAndStableHighLatencyDoNotTriggerDelayCuts() {
        AutoBitratePolicy.Feedback feedback = new AutoBitratePolicy.Feedback(true);
        for (int i = 0; i < 15; i++) {
            int rtt = (i == 0 || i == 7) ? 1 : 220;
            assertEquals(0, feedback.nextEncoderTarget(14000, 13500, 0, 60, rtt, i * 1000));
        }
        for (int i = 15; i < 30; i++) assertEquals(0, feedback.nextEncoderTarget(14000, 400, 0, 60, 400, i * 1000));
    }

    @Test public void aFailedIncreaseWaitsForALongerCleanRun() {
        AutoBitratePolicy.Feedback feedback = new AutoBitratePolicy.Feedback();
        int reduced = feedback.nextEncoderTarget(6000, 1200, 6, 30, 170, 0);
        int probe = 0;
        for (int i = 1; i <= 10; i++)
            probe = feedback.nextEncoderTarget(reduced, reduced, 0, 30, 170, 1000L * i);
        org.junit.Assert.assertTrue(probe > reduced);
        reduced = feedback.nextEncoderTarget(probe, 1200, 5, 30, 170, 14000);
        for (int i = 15; i < 34; i++)
            assertEquals(0, feedback.nextEncoderTarget(reduced, reduced, 0, 30, 170, i * 1000L));
        org.junit.Assert.assertTrue(feedback.nextEncoderTarget(reduced, reduced, 0, 30, 170, 34000) > reduced);
    }
}
