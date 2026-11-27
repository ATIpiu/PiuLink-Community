package com.limelight.piulink;

import android.view.KeyEvent;

import com.limelight.nvstream.input.KeyboardPacket;

public final class ShortcutChord {
    private final int keyCode;
    private final byte modifiers;

    private ShortcutChord(int keyCode, byte modifiers) {
        this.keyCode = keyCode;
        this.modifiers = modifiers;
    }

    public int getKeyCode() {
        return keyCode;
    }

    public byte getModifiers() {
        return modifiers;
    }

    public static ShortcutChord forAction(String action) {
        if (action == null) {
            return null;
        }
        switch (action) {
            case ShortcutRailConfiguration.WINDOW_NEXT:
                return new ShortcutChord(KeyEvent.KEYCODE_TAB, KeyboardPacket.MODIFIER_ALT);
            case ShortcutRailConfiguration.WINDOW_PREVIOUS:
                return new ShortcutChord(KeyEvent.KEYCODE_TAB,
                        (byte)(KeyboardPacket.MODIFIER_ALT | KeyboardPacket.MODIFIER_SHIFT));
            case ShortcutRailConfiguration.TASK_VIEW:
                return new ShortcutChord(KeyEvent.KEYCODE_TAB, KeyboardPacket.MODIFIER_META);
            case ShortcutRailConfiguration.SHOW_DESKTOP:
                return new ShortcutChord(KeyEvent.KEYCODE_D, KeyboardPacket.MODIFIER_META);
            case ShortcutRailConfiguration.CLOSE_WINDOW:
                return new ShortcutChord(KeyEvent.KEYCODE_F4, KeyboardPacket.MODIFIER_ALT);
            case ShortcutRailConfiguration.TASK_MANAGER:
                return new ShortcutChord(KeyEvent.KEYCODE_ESCAPE,
                        (byte)(KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_SHIFT));
            case "next_tab":
                return new ShortcutChord(KeyEvent.KEYCODE_TAB, KeyboardPacket.MODIFIER_CTRL);
            case "cut":
                return new ShortcutChord(KeyEvent.KEYCODE_X, KeyboardPacket.MODIFIER_CTRL);
            case "select_all":
                return new ShortcutChord(KeyEvent.KEYCODE_A, KeyboardPacket.MODIFIER_CTRL);
            case "find":
                return new ShortcutChord(KeyEvent.KEYCODE_F, KeyboardPacket.MODIFIER_CTRL);
            case "save":
                return new ShortcutChord(KeyEvent.KEYCODE_S, KeyboardPacket.MODIFIER_CTRL);
            case ShortcutRailConfiguration.COPY:
                return new ShortcutChord(KeyEvent.KEYCODE_C, KeyboardPacket.MODIFIER_CTRL);
            case ShortcutRailConfiguration.PASTE:
                return new ShortcutChord(KeyEvent.KEYCODE_V, KeyboardPacket.MODIFIER_CTRL);
            case ShortcutRailConfiguration.UNDO:
                return new ShortcutChord(KeyEvent.KEYCODE_Z, KeyboardPacket.MODIFIER_CTRL);
            case ShortcutRailConfiguration.ESCAPE:
                return new ShortcutChord(KeyEvent.KEYCODE_ESCAPE, (byte)0);
            default:
                return null;
        }
    }
}
