package com.limelight.piulink;
import org.junit.Test;
import java.util.Map;
import static org.junit.Assert.*;

public class RemoteKeyboardStateTest {
    @Test public void focusLossReleasesModifierAndHeldKeyExactlyOnce() {
        RemoteKeyboardState state = new RemoteKeyboardState();
        assertTrue(state.accept((short) 0x11, true, (byte) 1));
        assertTrue(state.accept((short) 0x41, true, (byte) 0));
        state.accept((short) 0x41, true, (byte) 0); // hardware repeat
        Map<Short, Byte> releases = state.releaseAll();
        assertEquals(2, releases.size());
        assertEquals(Byte.valueOf((byte) 1), releases.get((short) 0x11));
        assertTrue(state.releaseAll().isEmpty());
        assertFalse(state.accept((short) 0x41, false, (byte) 0)); // delayed release from old owner
    }
    @Test public void normalReleaseDoesNotLeakIntoTheNextConnection() {
        RemoteKeyboardState state = new RemoteKeyboardState();
        assertFalse(state.accept((short) 0x12, false, (byte) 0));
        state.accept((short) 0x12, true, (byte) 0);
        assertTrue(state.accept((short) 0x12, false, (byte) 0));
        assertTrue(state.releaseAll().isEmpty());
        assertTrue(state.accept((short) 0x12, true, (byte) 0));
        assertEquals(1, state.releaseAll().size());
    }
}
