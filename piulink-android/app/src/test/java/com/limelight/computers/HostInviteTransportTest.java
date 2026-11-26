package com.limelight.computers;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;
import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

import java.io.FileNotFoundException;
import java.io.IOException;
import java.net.SocketTimeoutException;
import javax.net.ssl.SSLHandshakeException;
import org.junit.Test;

public class HostInviteTransportTest {
    @Test public void customServiceComesFromTheInviteOrigin() {
        String fragment = "/piulink/join#" + new String(new char[64]).replace('\0','a') +
                ".12345678-1234-1234-1234-123456789abc";
        assertEquals("https://self.example:9443/piulink/",
                HostInvite.parse("https://self.example:9443" + fragment).serviceBase);
        assertNull(HostInvite.parseUri("https://user@self.example" + fragment));
        assertNull(HostInvite.parseUri("http://self.example" + fragment));
    }
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
