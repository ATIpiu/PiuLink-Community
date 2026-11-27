package com.limelight.piulink;

import android.view.KeyEvent;

import com.limelight.nvstream.input.KeyboardPacket;

import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;

public class ShortcutChordTest {
    @Test
    public void showDesktopIncludesTheWindowsModifier() {
        ShortcutChord chord = ShortcutChord.forAction(ShortcutRailConfiguration.SHOW_DESKTOP);

        assertEquals(KeyEvent.KEYCODE_D, chord.getKeyCode());
        assertEquals(KeyboardPacket.MODIFIER_META, chord.getModifiers());
    }

    @Test
    public void windowNextKeepsAltDownForThePressLifecycle() {
        ShortcutChord chord = ShortcutChord.forAction(ShortcutRailConfiguration.WINDOW_NEXT);

        assertEquals(KeyEvent.KEYCODE_TAB, chord.getKeyCode());
        assertEquals(KeyboardPacket.MODIFIER_ALT, chord.getModifiers());
    }

    @Test
    public void bottomRowEditingAndTabShortcutsUseControl() {
        String[] actions = {"next_tab", "cut", "select_all", "find", "save"};
        int[] keys = {KeyEvent.KEYCODE_TAB, KeyEvent.KEYCODE_X, KeyEvent.KEYCODE_A, KeyEvent.KEYCODE_F, KeyEvent.KEYCODE_S};
        for (int i = 0; i < actions.length; i++) {
            ShortcutChord chord = ShortcutChord.forAction(actions[i]);
            assertEquals(keys[i], chord.getKeyCode());
            assertEquals(KeyboardPacket.MODIFIER_CTRL, chord.getModifiers());
        }
    }

    @Test
    public void keyboardOverlayIsNotAKeyboardChord() {
        assertNull(ShortcutChord.forAction(ShortcutRailConfiguration.KEYBOARD));
    }
}
