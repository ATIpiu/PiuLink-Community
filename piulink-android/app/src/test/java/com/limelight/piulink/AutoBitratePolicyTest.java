package com.limelight.piulink;

import org.junit.Test;

import static org.junit.Assert.assertEquals;

public class AutoBitratePolicyTest {
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
                "100.64.0.42", true, false, true));
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
