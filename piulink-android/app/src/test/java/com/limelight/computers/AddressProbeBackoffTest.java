package com.limelight.computers;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

public class AddressProbeBackoffTest {
    private static final class FakeClock implements AddressProbeBackoff.Clock {
        long now;

        @Override
        public long elapsedRealtime() {
            return now;
        }
    }

    @Test
    public void backsOffAfterConsecutiveFailuresAndResetsOnSuccess() {
        FakeClock clock = new FakeClock();
        AddressProbeBackoff backoff = new AddressProbeBackoff(clock);

        backoff.recordFailure("unreachable-v6");
        assertTrue(backoff.shouldProbe("unreachable-v6", false));

        backoff.recordFailure("unreachable-v6");
        assertFalse(backoff.shouldProbe("unreachable-v6", false));
        assertEquals(5_000, backoff.remainingDelayMs("unreachable-v6"));

        clock.now = 5_000;
        assertTrue(backoff.shouldProbe("unreachable-v6", false));
        backoff.recordFailure("unreachable-v6");
        assertEquals(10_000, backoff.remainingDelayMs("unreachable-v6"));

        backoff.recordSuccess("unreachable-v6");
        assertTrue(backoff.shouldProbe("unreachable-v6", false));
    }

    @Test
    public void tracksAddressesIndependentlyAndCapsDelay() {
        FakeClock clock = new FakeClock();
        AddressProbeBackoff backoff = new AddressProbeBackoff(clock);

        for (int i = 0; i < 30; i++) {
            backoff.recordFailure("bad-v6");
        }

        assertEquals(AddressProbeBackoff.MAX_DELAY_MS, backoff.remainingDelayMs("bad-v6"));
        assertTrue(backoff.shouldProbe("good-v4", false));
    }

    @Test
    public void networkChangeMakesFailedAddressesImmediatelyEligible() {
        FakeClock clock = new FakeClock();
        AddressProbeBackoff backoff = new AddressProbeBackoff(clock);
        backoff.recordFailure("bad-v6");
        backoff.recordFailure("bad-v6");

        backoff.resetForNetworkChange();

        assertTrue(backoff.shouldProbe("bad-v6", false));
        assertEquals(0, backoff.remainingDelayMs("bad-v6"));
    }

    @Test
    public void newlyOpenedTunnelGetsFirstHostProbeDuringOldListenerCooldown() {
        AddressProbeBackoff backoff = new AddressProbeBackoff(new FakeClock());
        String localAdapter = "127.42.17.31:47989";
        for (int i = 0; i < 12; i++) backoff.recordFailure(localAdapter);
        assertEquals(AddressProbeBackoff.MAX_DELAY_MS, backoff.remainingDelayMs(localAdapter));
        assertFalse(backoff.shouldProbe(localAdapter, false));
        assertTrue(backoff.shouldProbe(localAdapter, true));
        backoff.recordSuccess(localAdapter);
        assertTrue(backoff.shouldProbe(localAdapter, false));
    }
}
