package com.limelight.piulink;

import org.junit.Test;

import static org.junit.Assert.assertEquals;

public class NetworkPathClassifierTest {
    @Test
    public void classifiesObservableStreamDestinationWithoutDns() {
        assertEquals(NetworkPathClassifier.Path.IPV4_DIRECT,
                NetworkPathClassifier.classify("203.0.113.8"));
        assertEquals(NetworkPathClassifier.Path.IPV6_DIRECT,
                NetworkPathClassifier.classify("[2001:db8::8]"));
        assertEquals(NetworkPathClassifier.Path.TAILSCALE_MANAGED,
                NetworkPathClassifier.classify("100.100.20.30"));
        assertEquals(NetworkPathClassifier.Path.TAILSCALE_MANAGED,
                NetworkPathClassifier.classify("fd7a:115c:a1e0::20"));
        assertEquals(NetworkPathClassifier.Path.TAILSCALE_MANAGED,
                NetworkPathClassifier.classify("gaming-pc.tail123.ts.net"));
        assertEquals(NetworkPathClassifier.Path.HOSTNAME_UNKNOWN,
                NetworkPathClassifier.classify("gaming-pc.local"));
    }
}
