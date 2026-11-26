package com.limelight.piulink;

final class AutoBitratePolicy {
    static final int MIN_BITRATE_KBPS = 3_000;
    private static final int MAX_BITRATE_KBPS = 150_000;

    private AutoBitratePolicy() {
    }

    // Choose the handshake bitrate once. Android's link bandwidth estimates the
    // local bearer, not throughput across a VPN or the Internet.
    static int startupBitrate(String route, int width, int height, int fps,
                              int downstreamEstimateKbps, boolean saveDataOnMeteredNetwork,
                              String tuningMode) {
        long pixelsPerSecond = (long) Math.max(1, width) * Math.max(1, height) * Math.max(1, fps);
        int base = pixelsPerSecond >= 3840L * 2160 * 60 ? 60_000 :
                pixelsPerSecond >= 2560L * 1440 * 60 ? 45_000 :
                pixelsPerSecond >= 1920L * 1080 * 60 ? 30_000 : 20_000;
        int target = "local".equals(route) ? base : 8_000;
        if ("quality".equals(tuningMode)) target = "local".equals(route) ? target * 125 / 100 : 14_000;
        else if ("stable".equals(tuningMode)) target = target * 75 / 100;
        // Only a direct local route shares the estimated bearer with the Host.
        if ("local".equals(route) && downstreamEstimateKbps > 0)
            target = Math.min(target, downstreamEstimateKbps * 70 / 100);
        if ("metered".equals(route) && saveDataOnMeteredNetwork)
            target = Math.min(target, 10_000);
        target = Math.max(MIN_BITRATE_KBPS, Math.min(MAX_BITRATE_KBPS, target));
        return Math.max(MIN_BITRATE_KBPS, target / 500 * 500);
    }

    static DisplayInventoryClient.Mode fitTvOutput(DisplayInventoryClient.Mode stream,
                                                    java.util.List<DisplayInventoryClient.Mode> outputs) {
        if (stream.width <= 0 || stream.height <= 0) return stream;
        double bestScale = 0;
        int bestFps = 0;
        for (DisplayInventoryClient.Mode output : outputs) {
            if (output.width <= 0 || output.height <= 0 || output.fps <= 0) continue;
            int fps = Math.min(stream.fps, output.fps);
            double scale = Math.min(1d, Math.min((double) output.width / stream.width,
                    (double) output.height / stream.height));
            if (fps > bestFps || (fps == bestFps && scale > bestScale)) {
                bestFps = fps;
                bestScale = scale;
            }
        }
        if (bestFps == 0 || (bestScale == 1 && bestFps == stream.fps)) return stream;
        return new DisplayInventoryClient.Mode(Math.max(2, (int) (stream.width * bestScale) & ~1),
                Math.max(2, (int) (stream.height * bestScale) & ~1), bestFps);
    }

    static DisplayInventoryClient.Mode physicalMode(String route, int width, int height,
                                                     boolean automatic, String preference, boolean saveData) {
        if (!automatic || "local".equals(route) || width <= 0 || height <= 0)
            return new DisplayInventoryClient.Mode(width, height, 60);
        boolean saving = "stable".equals(preference) || ("metered".equals(route) && saveData);
        boolean quality = !saving && "quality".equals(preference);
        int longEdge = saving ? 1280 : quality ? 2560 : 1920;
        int shortEdge = saving ? 720 : quality ? 1440 : 1080;
        double scale = Math.min(1d, Math.min((double) longEdge / Math.max(width, height),
                (double) shortEdge / Math.min(width, height)));
        return new DisplayInventoryClient.Mode(Math.max(2, (int)(width * scale) & ~1),
                Math.max(2, (int)(height * scale) & ~1), quality ? 60 : 30);
    }

    static final class Feedback {
        private int encoderCeiling;
        private int lossyWindows;
        private int cleanWindows;
        private long nextChangeMs;
        private long lastIncreaseMs = -1;
        private int recoveryDelay;
        private final boolean watchVpnDelay;
        private double baselineRtt;
        private int rttSamples, previousRtt, olderRtt, delayedWindows, clearWindows;
        private boolean delayReductionApplied;

        Feedback() { this(false); }
        Feedback(boolean watchVpnDelay) { this.watchVpnDelay = watchVpnDelay; }

        private boolean sustainedQueueDelay(int rttMs, boolean carryingVideo) {
            if (!watchVpnDelay || rttMs <= 0) {
                delayedWindows = clearWindows = 0;
                return false;
            }
            // Compare with this session's own RTT. A stable, long-distance VPN is healthy.
            boolean elevated = rttSamples >= 3 && rttMs > baselineRtt + Math.max(25, baselineRtt * 0.5);
            if (rttSamples >= 2) {
                // Use a three-sample median so an isolated very low RTT cannot set the baseline.
                int middle = Math.max(Math.min(olderRtt, previousRtt), Math.min(rttMs, Math.max(olderRtt, previousRtt)));
                if (baselineRtt == 0 || middle < baselineRtt) baselineRtt = middle;
                else baselineRtt += Math.min(1.0, (middle - baselineRtt) * 0.04);
            }
            olderRtt = previousRtt;
            previousRtt = rttMs;
            if (rttSamples < 3) rttSamples++;
            delayedWindows = elevated && carryingVideo ? delayedWindows + 1 : 0;
            clearWindows = elevated ? 0 : clearWindows + 1;
            if (clearWindows >= 3) delayReductionApplied = false;
            return elevated;
        }

        // Only act on an encoder-confirmed rate. The handshake budget also covers FEC and audio.
        int nextEncoderTarget(int applied, int received, int lost, int frames, int rttMs, long nowMs) {
            if (applied <= 0) return 0;
            if (encoderCeiling == 0) encoderCeiling = applied;
            if (frames < 8 || lost < 0 || lost > frames || received <= 0) {
                lossyWindows = cleanWindows = delayedWindows = clearWindows = 0;
                return 0;
            }
            float loss = (float) lost / frames;
            lossyWindows = loss >= 0.03f ? lossyWindows + 1 : 0;
            boolean carryingVideo = received >= applied * 0.55f;
            boolean elevated = sustainedQueueDelay(rttMs, carryingVideo);
            cleanWindows = lost == 0 && carryingVideo && !elevated ? cleanWindows + 1 : 0;
            if (nowMs < nextChangeMs) return 0;
            int target;
            if (loss >= 0.10f || lossyWindows >= 2) {
                target = Math.max(Math.min(750, encoderCeiling), applied * (loss >= 0.10f ? 55 : 80) / 100);
                // A link that lost frames immediately after a probe needs a longer stable interval.
                if (lastIncreaseMs >= 0 && nowMs - lastIncreaseMs < 15_000)
                    recoveryDelay = Math.min(40, recoveryDelay + 10);
                lastIncreaseMs = -1;
                if (elevated) delayReductionApplied = true;
            } else if (delayedWindows >= 3 && !delayReductionApplied) {
                target = Math.max(Math.min(750, encoderCeiling), applied * 80 / 100);
                delayReductionApplied = true;
                lastIncreaseMs = -1;
            } else if (cleanWindows >= (rttMs >= 200 ? 18 : 10) + recoveryDelay) {
                target = Math.min(encoderCeiling, applied + Math.max(200, applied / 6));
                if (target > applied) {
                    if (lastIncreaseMs >= 0) recoveryDelay = Math.max(0, recoveryDelay - 10);
                    lastIncreaseMs = nowMs;
                }
            } else return 0;
            if (target == applied) return 0;
            nextChangeMs = nowMs + 4000;
            lossyWindows = cleanWindows = 0;
            return target;
        }
    }
}
