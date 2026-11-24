package com.limelight.piulink;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public class P2pReconnectPolicyTest {
    @Test
    public void offlineTvNeverStartsDisplayPreparationOnTimeout() {
        assertEquals(P2pReconnectPolicy.TvNetworkStep.WAIT, P2pReconnectPolicy.tvNetworkStep(false, 11_999));
        assertEquals(P2pReconnectPolicy.TvNetworkStep.OFFER_RETRY, P2pReconnectPolicy.tvNetworkStep(false, 12_000));
        assertEquals(P2pReconnectPolicy.TvNetworkStep.CONNECT, P2pReconnectPolicy.tvNetworkStep(true, 11_999));
        assertEquals(P2pReconnectPolicy.TvNetworkStep.CONNECT, P2pReconnectPolicy.tvNetworkStep(true, 12_001));
    }

    @Test
    public void repeatedTransportLossOffersManualRecoveryWhileForeground() {
        assertTrue(P2pReconnectPolicy.offerManualTvRetry(-1, true, 1, false));
        assertFalse(P2pReconnectPolicy.offerManualTvRetry(-1, true, 1, true));
        assertFalse(P2pReconnectPolicy.offerManualTvRetry(-1, false, 1, false));
        assertFalse(P2pReconnectPolicy.offerManualTvRetry(-101, true, 1, false));
        assertFalse(P2pReconnectPolicy.offerManualTvRetry(-1, true, 0, false));
    }

    @Test
    public void retriesOneTransientPrivatePathFailure() {
        assertTrue(P2pReconnectPolicy.shouldRetry(-100, true, 0, false));
        assertFalse(P2pReconnectPolicy.shouldRetry(-100, true, 1, false));
    }

    @Test
    public void doesNotRetryUserOrDecoderFailures() {
        assertFalse(P2pReconnectPolicy.shouldRetry(0, true, 0, false));
        assertFalse(P2pReconnectPolicy.shouldRetry(-103, true, 0, false));
        assertFalse(P2pReconnectPolicy.shouldRetry(-104, true, 0, false));
    }

    @Test
    public void doesNotRetryPublicOrFinishingActivity() {
        assertFalse(P2pReconnectPolicy.shouldRetry(-100, false, 0, false));
        assertFalse(P2pReconnectPolicy.shouldRetry(-100, true, 0, true));
    }
}
