package com.limelight.computers;

import java.util.HashMap;
import java.util.Map;

/** Keeps repeated background probes from hammering an address that is known to be unreachable. */
final class AddressProbeBackoff {
    static final long INITIAL_DELAY_MS = 5_000;
    static final long MAX_DELAY_MS = 5 * 60_000;
    static final int FAILURES_BEFORE_BACKOFF = 2;

    interface Clock {
        long elapsedRealtime();
    }

    private static final class State {
        int consecutiveFailures;
        long retryAfterMs;
    }

    private final Clock clock;
    private final Map<String, State> states = new HashMap<>();

    AddressProbeBackoff(Clock clock) {
        this.clock = clock;
    }

    synchronized boolean shouldProbe(String address, boolean activeTunnel) {
        // A saved loopback address outlives its listener. A newly authenticated
        // tunnel at that address needs a real Host probe even during an old cooldown.
        if (activeTunnel) return true;
        State state = states.get(address);
        return state == null || clock.elapsedRealtime() >= state.retryAfterMs;
    }

    synchronized long remainingDelayMs(String address) {
        State state = states.get(address);
        return state == null ? 0 : Math.max(0, state.retryAfterMs - clock.elapsedRealtime());
    }

    synchronized void recordSuccess(String address) {
        states.remove(address);
    }

    synchronized void recordFailure(String address) {
        State state = states.computeIfAbsent(address, ignored -> new State());
        state.consecutiveFailures++;
        if (state.consecutiveFailures < FAILURES_BEFORE_BACKOFF) {
            state.retryAfterMs = 0;
            return;
        }

        int exponent = Math.min(state.consecutiveFailures - FAILURES_BEFORE_BACKOFF, 16);
        long delay = Math.min(MAX_DELAY_MS, INITIAL_DELAY_MS << exponent);
        state.retryAfterMs = clock.elapsedRealtime() + delay;
    }

    synchronized void resetForNetworkChange() {
        states.clear();
    }
}
