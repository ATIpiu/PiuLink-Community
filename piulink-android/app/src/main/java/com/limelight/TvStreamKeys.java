package com.limelight;

import android.view.InputDevice;
import android.view.KeyEvent;

import java.util.HashMap;

/** Keep both halves of a local menu gesture on the TV when its key-down changes focus. */
final class TvStreamKeys {
    private final HashMap<Long, Long> localPresses = new HashMap<>();

    static boolean controller(int sources) {
        return (sources & InputDevice.SOURCE_GAMEPAD) == InputDevice.SOURCE_GAMEPAD
                || (sources & InputDevice.SOURCE_JOYSTICK) == InputDevice.SOURCE_JOYSTICK;
    }

    static boolean east(int code, int scan, int sources) {
        return code == KeyEvent.KEYCODE_BUTTON_B || (scan == 305 && controller(sources));
    }

    void ownDown(int device, int key, long downTime) {
        localPresses.put(identity(device, key), downTime);
    }

    boolean ownsFollowup(int device, int key, long downTime, boolean up) {
        long identity = identity(device, key);
        Long started = localPresses.get(identity);
        if (started == null) return false;
        if (up || started != downTime) localPresses.remove(identity);
        return started == downTime;
    }

    private static long identity(int device, int key) {
        return ((long) device << 32) | (key & 0xffffffffL);
    }
}
