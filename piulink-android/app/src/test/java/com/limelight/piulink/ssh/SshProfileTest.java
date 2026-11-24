package com.limelight.piulink.ssh;

import org.junit.Test;
import static org.junit.Assert.*;

public class SshProfileTest {
    @Test public void malformedRecordNeverReturnsAPartialList() {
        String valid = "{\"host\":\"pc\",\"user\":\"user\",\"port\":22}";
        assertEquals(2, SshProfile.decode("[" + valid + "," + valid + "]").size());
        for (String json : new String[] {"null", "[", "[" + valid + ",{}]",
                "[{\"host\":123,\"user\":\"u\",\"port\":22}]",
                "[{\"host\":\"pc\",\"user\":\"u\",\"port\":22.5}]"}) {
            try { SshProfile.decode(json); fail("Damaged profiles accepted: " + json); }
            catch (IllegalStateException expected) { }
        }
    }
    @Test public void multilineInputCannotBypassValidation() {
        for (String value : new String[] {"a\nb\nc", "a\rb\nc", "a\u0000b", "a\tb", "a\u007fb"}) {
            try { new SshProfile(value, "user", 22); fail("Invalid host accepted"); }
            catch (IllegalArgumentException expected) { }
            try { new SshProfile("host", value, 22); fail("Invalid user accepted"); }
            catch (IllegalArgumentException expected) { }
        }
    }
    @Test public void validIpv6AndTrimmedCredentialsArePreserved() {
        SshProfile profile = new SshProfile("  fd00::1  ", "  user  ", 2222);
        assertEquals("fd00::1", profile.host);
        assertEquals("user", profile.user);
        assertEquals(2222, profile.port);
    }
}
