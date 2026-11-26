package com.limelight.preferences;

import java.net.InetAddress;
import java.net.URI;
import org.junit.Test;
import static org.junit.Assert.*;

public class ManualAddressTest {
    @Test public void acceptsCommonKeyboardSeparatorsAndStillValidatesTheAddress() {
        URI address = AddComputerManually.parseRawUserInputToUri("192。168．50｡8：47989");
        assertEquals("192.168.50.8", address.getHost());
        assertEquals(47989, address.getPort());
        assertEquals("pc.lan", AddComputerManually.parseRawUserInputToUri("pc。lan").getHost());
        assertNull(AddComputerManually.parseRawUserInputToUri("pc。lan：65536"));
        assertNull(AddComputerManually.parseRawUserInputToUri("pc。lan/extra"));
    }

    @Test public void namesPortsAndIpv6() {
        assertEquals("pc.lan", AddComputerManually.parseRawUserInputToUri(" pc.lan ").getHost());
        assertEquals(1, AddComputerManually.parseRawUserInputToUri("10.1.2.3:1").getPort());
        URI v6 = AddComputerManually.parseRawUserInputToUri("[2001:db8::1]:65535");
        assertEquals(65535, v6.getPort());
        assertEquals(-1, AddComputerManually.parseRawUserInputToUri("2001:db8::").getPort());
        assertEquals(-1, AddComputerManually.parseRawUserInputToUri("::1").getPort());
    }

    @Test public void rejectsAmbiguousOrIncompleteAddresses() {
        for (String raw : new String[]{"", "pc:", "pc:0", "pc:65536", "pc:x", "pc:9999999999",
                "https://pc", "me@pc", "pc/path", "pc?query", "pc#name", "[::1]:", "not a host"}) {
            assertNull(raw, AddComputerManually.parseRawUserInputToUri(raw));
        }
    }

    @Test public void networkPrefixesCompareHighBitsFirst() throws Exception {
        byte[] base = InetAddress.getByName("192.168.16.129").getAddress();
        assertTrue(AddComputerManually.matchesPrefix(base, InetAddress.getByName("192.168.31.1").getAddress(), 20));
        assertFalse(AddComputerManually.matchesPrefix(base, InetAddress.getByName("192.168.32.129").getAddress(), 20));
        assertTrue(AddComputerManually.matchesPrefix(base, InetAddress.getByName("192.168.16.254").getAddress(), 25));
        assertFalse(AddComputerManually.matchesPrefix(base, InetAddress.getByName("192.168.16.1").getAddress(), 25));
        assertTrue(AddComputerManually.matchesPrefix(base, base, 32));
        assertFalse(AddComputerManually.matchesPrefix(base, base, 33));
    }
}
