package com.limelight.computers;

import org.junit.Test;
import java.io.EOFException;
import java.io.IOException;
import java.net.SocketTimeoutException;
import javax.net.ssl.SSLHandshakeException;
import static org.junit.Assert.*;

public class DesktopLookupTest {
    @Test public void retriesTruncatedAuthenticatedResponse() {
        assertTrue(DesktopLookup.isTruncatedRead(new IOException("unexpected end of stream", new EOFException())));
    }

    @Test public void keepsTlsAndTimeoutFailures() {
        SSLHandshakeException handshake = new SSLHandshakeException("TLS failed");
        handshake.initCause(new EOFException());
        assertFalse(DesktopLookup.isTruncatedRead(handshake));
        assertFalse(DesktopLookup.isTruncatedRead(new SocketTimeoutException()));
    }
}
