package com.limelight.computers;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.io.FileNotFoundException;
import java.io.IOException;
import java.net.SocketTimeoutException;
import javax.net.ssl.SSLHandshakeException;
import org.junit.Test;

public class HostInviteTransportTest {
    @Test public void onlyTemporaryFailuresQualifyForRetryOrPairedLanRecovery() {
        assertTrue(HostInvite.isTransient(new IOException("read failed", new SocketTimeoutException())));
        assertTrue(HostInvite.isTransient(new HostInvite.ServiceUnavailable(503)));
        assertFalse(HostInvite.isTransient(new FileNotFoundException("Host offline")));
        assertFalse(HostInvite.isTransient(new IOException("Wrong Host identity")));
        assertFalse(HostInvite.isTransient(new SSLHandshakeException("Invalid certificate")));
    }

    @Test public void cancelStopsFurtherNetworkAttempts() {
        Thread.currentThread().interrupt();
        try { assertFalse(HostInvite.isTransient(new SocketTimeoutException())); }
        finally { Thread.interrupted(); }
    }
}
