package com.limelight.utils;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import org.junit.Test;

import java.io.IOException;
import java.net.SocketException;

public class NetHelperTest {
    @Test
    public void detectsVendorNetworkBindingDenialInCauseChain() {
        IOException wrapper = new IOException("request failed",
                new SocketException("Binding socket to network 103 failed: EPERM (Operation not permitted)"));
        assertTrue(NetHelper.isNetworkBindingDenied(wrapper));
    }

    @Test
    public void doesNotTreatOrdinaryConnectionFailureAsBindingDenial() {
        assertFalse(NetHelper.isNetworkBindingDenied(
                new IOException("Failed to connect to 192.168.1.8")));
    }

    @Test
    public void stripsIpv6ZoneAndFormatsAuthorityWithoutDoubleBracketing() {
        assertEquals("fe80::1", NetHelper.stripIpv6Zone("[fe80::1%wlan0]"));
        assertEquals("[fe80::1]", NetHelper.formatAuthorityHost("fe80::1%wlan0"));
        assertEquals("[2001:db8::1]", NetHelper.formatAuthorityHost("[2001:db8::1]"));
        assertEquals("192.168.1.8", NetHelper.formatAuthorityHost("192.168.1.8"));
    }

    @Test
    public void remembersNonZeroDeniedDirectNetworkHandleUntilReset() {
        NetHelper.resetDirectNetworkBindingState();
        try {
            assertFalse(NetHelper.isDirectNetworkHandleDenied(0L));
            assertFalse(NetHelper.isDirectNetworkHandleDenied(103L));
            NetHelper.noteDirectNetworkBindingDenied(null);
            assertFalse(NetHelper.isDirectNetworkHandleDenied(0L));
            NetHelper.setDeniedDirectNetworkHandleForTest(103L);
            assertTrue(NetHelper.isDirectNetworkHandleDenied(103L));
            assertFalse(NetHelper.isDirectNetworkHandleDenied(104L));
            assertFalse(NetHelper.isDirectNetworkHandleDenied(0L));
        } finally {
            NetHelper.resetDirectNetworkBindingState();
        }
        assertFalse(NetHelper.isDirectNetworkHandleDenied(103L));
    }
}
