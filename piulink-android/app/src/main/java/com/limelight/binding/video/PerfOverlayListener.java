package com.limelight.binding.video;

public interface PerfOverlayListener {
    void onPerfUpdate(final String text);
    void onVideoSizeRendered(int width, int height);
    void onVideoThroughputSample(int receivedKbps, int lost, int frames, int rttMs, long sampledAtMs);
}
