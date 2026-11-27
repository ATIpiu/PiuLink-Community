package com.limelight.computers;

import com.limelight.nvstream.http.ComputerDetails.AddressTuple;
import org.junit.Test;
import static org.junit.Assert.*;

public class ConnectionHistoryEndpointTest {
    @Test public void readsStoredAddressesWithoutResolvingDns() {
        assertEquals(new AddressTuple("192.0.2.10", 47989), ConnectionHistory.endpoint("192.0.2.10:47989"));
        assertEquals(new AddressTuple("2001:db8::10", 47989), ConnectionHistory.endpoint("[2001:db8::10]:47989"));
        assertEquals(new AddressTuple("desktop.invalid", 48000), ConnectionHistory.endpoint("desktop.invalid:48000"));
    }

    @Test public void skipsInvitationsAndMalformedHistoryEntries() {
        for (String value : new String[]{null, "", "https://example.org/invite", "pc:0", "pc:65536",
                "pc:47989/extra", "user@pc:47989", "pc:47989#room", "pc:47989?q=1"}) {
            assertNull(value, ConnectionHistory.endpoint(value));
        }
    }
}
