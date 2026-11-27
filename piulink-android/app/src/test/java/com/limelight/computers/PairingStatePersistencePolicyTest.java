package com.limelight.computers;

import static com.limelight.nvstream.http.PairingManager.PairState.NOT_PAIRED;
import static com.limelight.nvstream.http.PairingManager.PairState.PAIRED;
import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class PairingStatePersistencePolicyTest {
    @Test
    public void publicPollPreservesTrustOnlyWhenCertificateExists() {
        assertEquals(PAIRED, PairingStatePersistencePolicy.resolvePublicPollState(true));
        assertEquals(NOT_PAIRED, PairingStatePersistencePolicy.resolvePublicPollState(false));
    }
}
