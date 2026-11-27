package com.limelight.computers;

import com.limelight.nvstream.http.PairingManager.PairState;

/** Keeps authenticated pairing state separate from unauthenticated discovery metadata. */
final class PairingStatePersistencePolicy {
    private PairingStatePersistencePolicy() {
    }

    static PairState resolvePublicPollState(boolean hasPersistedCertificate) {
        return hasPersistedCertificate ? PairState.PAIRED : PairState.NOT_PAIRED;
    }
}
