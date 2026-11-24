package com.limelight.computers;

import com.limelight.nvstream.http.PairingManager.PairState;

/** Keeps authenticated pairing state separate from unauthenticated discovery metadata. */
final class PairingStatePersistencePolicy {
    private PairingStatePersistencePolicy() {
    }

    static PairState resolvePublicPollState(PairState persistedState,
                                            boolean hasPersistedCertificate,
                                            PairState publicPollState) {
        // Sunshine's public HTTP serverinfo reports PairStatus=0 even for paired clients.
        // On process restart pairState is transient and has not yet been restored, while the
        // pinned certificate has already been loaded from storage. Only an authenticated HTTPS
        // operation may invalidate that certificate; public discovery must never downgrade it.
        if (hasPersistedCertificate) {
            return PairState.PAIRED;
        }
        return publicPollState;
    }
}
