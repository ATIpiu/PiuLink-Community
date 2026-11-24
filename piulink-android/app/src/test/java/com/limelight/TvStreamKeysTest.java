package com.limelight;

import android.view.InputDevice;
import android.view.KeyEvent;

import org.junit.Test;

import static org.junit.Assert.*;

public class TvStreamKeysTest {
    @Test public void remappedEastButtonBelongsToGamepad() {
        assertTrue(TvStreamKeys.east(KeyEvent.KEYCODE_BUTTON_B, 0, 0));
        assertTrue(TvStreamKeys.east(KeyEvent.KEYCODE_BACK, 305, InputDevice.SOURCE_GAMEPAD));
        assertFalse(TvStreamKeys.east(KeyEvent.KEYCODE_BACK, 305, InputDevice.SOURCE_DPAD));
        assertFalse(TvStreamKeys.controller(InputDevice.SOURCE_DPAD | InputDevice.SOURCE_KEYBOARD));
        assertTrue(TvStreamKeys.controller(InputDevice.SOURCE_JOYSTICK | InputDevice.SOURCE_DPAD));
    }

    @Test public void closingMenuConsumesOnlyThatDevicesMatchingRelease() {
        TvStreamKeys keys = new TvStreamKeys();
        keys.ownDown(2, KeyEvent.KEYCODE_BUTTON_B, 123);
        assertFalse(keys.ownsFollowup(3, KeyEvent.KEYCODE_BUTTON_B, 123, true));
        assertTrue(keys.ownsFollowup(2, KeyEvent.KEYCODE_BUTTON_B, 123, false));
        assertTrue(keys.ownsFollowup(2, KeyEvent.KEYCODE_BUTTON_B, 123, true));
        assertFalse(keys.ownsFollowup(2, KeyEvent.KEYCODE_BUTTON_B, 125, false));
    }

    @Test public void missingKeyUpDoesNotSwallowNextPress() {
        TvStreamKeys keys = new TvStreamKeys();
        keys.ownDown(2, KeyEvent.KEYCODE_BUTTON_A, 123);
        assertFalse(keys.ownsFollowup(2, KeyEvent.KEYCODE_BUTTON_A, 200, false));
        assertFalse(keys.ownsFollowup(2, KeyEvent.KEYCODE_BUTTON_A, 200, true));
    }
}
