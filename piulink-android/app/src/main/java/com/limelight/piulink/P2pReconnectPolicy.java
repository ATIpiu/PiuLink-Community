package com.limelight.piulink;

/** Bounds automatic recovery to transient failures on a private peer-to-peer path. */
public final class P2pReconnectPolicy {
    private static final int GRACEFUL_TERMINATION = 0;
    private static final int PROTECTED_CONTENT = -103;
    private static final int FRAME_CONVERSION = -104;

    private P2pReconnectPolicy() { }

    public enum TvNetworkStep { WAIT, CONNECT, OFFER_RETRY }

    public static TvNetworkStep tvNetworkStep(boolean hasNetwork, long elapsedMs) {
        if (hasNetwork) return TvNetworkStep.CONNECT;
        return elapsedMs < 12_000 ? TvNetworkStep.WAIT : TvNetworkStep.OFFER_RETRY;
    }

    public static boolean offerManualTvRetry(int errorCode, boolean privatePeerPath,
                                             int priorAttempts, boolean activityFinishing) {
        return errorCode == -1 && privatePeerPath && priorAttempts >= 1 && !activityFinishing;
    }

    public static boolean shouldRetry(int errorCode, boolean privatePeerPath,
                                      int priorAttempts, boolean activityFinishing) {
        return privatePeerPath && !activityFinishing && priorAttempts < 1 &&
                errorCode != GRACEFUL_TERMINATION &&
                errorCode != PROTECTED_CONTENT &&
                errorCode != FRAME_CONVERSION;
    }
}
