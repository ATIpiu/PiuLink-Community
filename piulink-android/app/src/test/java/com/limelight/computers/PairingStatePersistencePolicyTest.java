package com.limelight.computers;

import static com.limelight.nvstream.http.PairingManager.PairState.NOT_PAIRED;
import static com.limelight.nvstream.http.PairingManager.PairState.PAIRED;
import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class PairingStatePersistencePolicyTest {
    @Test
    public void publicUnpairedStatusCannotErasePersistedPairing() {
        assertEquals(PAIRED, PairingStatePersistencePolicy.resolvePublicPollState(
                PAIRED, true, NOT_PAIRED));
        // pairState is process-local and starts out unset/unpaired after Android restarts, but
        // the database has already restored the pinned Host certificate.
        assertEquals(PAIRED, PairingStatePersistencePolicy.resolvePublicPollState(
                NOT_PAIRED, true, NOT_PAIRED));
    }

    @Test
    public void publicStatusIsUsedWithoutPersistedPairingEvidence() {
        assertEquals(NOT_PAIRED, PairingStatePersistencePolicy.resolvePublicPollState(
                NOT_PAIRED, false, NOT_PAIRED));
        assertEquals(PAIRED, PairingStatePersistencePolicy.resolvePublicPollState(
                NOT_PAIRED, false, PAIRED));
    }
}
