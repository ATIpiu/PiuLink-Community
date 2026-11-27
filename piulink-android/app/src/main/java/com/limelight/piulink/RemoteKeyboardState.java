package com.limelight.piulink;

import java.util.LinkedHashMap;
import java.util.Map;

/** Tracks exactly the keys whose down events were forwarded to this connection. */
public final class RemoteKeyboardState {
    private final Map<Short, Byte> pressed = new LinkedHashMap<>();
    public synchronized boolean accept(short key, boolean down, byte flags) {
        if (down) { pressed.put(key, flags); return true; }
        return pressed.remove(key) != null;
    }
    public synchronized boolean isPressed(short key) {
        return pressed.containsKey(key);
    }
    public synchronized Map<Short, Byte> releaseAll() {
        Map<Short, Byte> released = new LinkedHashMap<>(pressed);
        pressed.clear();
        return released;
    }
}
