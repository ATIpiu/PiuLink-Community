package com.limelight.piulink;

import org.junit.Test;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertTrue;

public class ShortcutRailConfigurationTest {
    @Test
    public void defaultsExposeTheCommonWindowsActions() {
        assertEquals(new HashSet<>(Arrays.asList(
                        ShortcutRailConfiguration.WINDOW_NEXT,
                        ShortcutRailConfiguration.WINDOW_PREVIOUS,
                        ShortcutRailConfiguration.TASK_VIEW,
                        ShortcutRailConfiguration.SHOW_DESKTOP,
                        ShortcutRailConfiguration.CLOSE_WINDOW,
                        ShortcutRailConfiguration.TASK_MANAGER,
                        ShortcutRailConfiguration.COPY,
                        ShortcutRailConfiguration.PASTE,
                        ShortcutRailConfiguration.ESCAPE,
                        ShortcutRailConfiguration.KEYBOARD,
                        ShortcutRailConfiguration.VOICE_INPUT)),
                ShortcutRailConfiguration.defaults());
    }

    @Test
    public void selectionUsesAStableProductOrder() {
        List<String> ordered = ShortcutRailConfiguration.orderedSelection(new HashSet<>(Arrays.asList(
                ShortcutRailConfiguration.KEYBOARD,
                ShortcutRailConfiguration.WINDOW_PREVIOUS,
                ShortcutRailConfiguration.WINDOW_NEXT)));

        assertEquals(Arrays.asList(
                ShortcutRailConfiguration.WINDOW_NEXT,
                ShortcutRailConfiguration.WINDOW_PREVIOUS,
                ShortcutRailConfiguration.KEYBOARD), ordered);
    }

    @Test
    public void legacyDefaultsAreExpandedButCustomSelectionsArePreserved() {
        HashSet<String> legacy = new HashSet<>(Arrays.asList(
                ShortcutRailConfiguration.WINDOW_NEXT,
                ShortcutRailConfiguration.TASK_VIEW,
                ShortcutRailConfiguration.SHOW_DESKTOP,
                ShortcutRailConfiguration.KEYBOARD));
        assertEquals(ShortcutRailConfiguration.defaults(),
                ShortcutRailConfiguration.migrateLegacyDefaults(legacy));

        HashSet<String> previousDefault = new HashSet<>(Arrays.asList(
                ShortcutRailConfiguration.WINDOW_NEXT,
                ShortcutRailConfiguration.WINDOW_PREVIOUS,
                ShortcutRailConfiguration.TASK_VIEW,
                ShortcutRailConfiguration.SHOW_DESKTOP,
                ShortcutRailConfiguration.CLOSE_WINDOW,
                ShortcutRailConfiguration.TASK_MANAGER,
                ShortcutRailConfiguration.COPY,
                ShortcutRailConfiguration.PASTE,
                ShortcutRailConfiguration.ESCAPE,
                ShortcutRailConfiguration.KEYBOARD));
        assertEquals(ShortcutRailConfiguration.defaults(),
                ShortcutRailConfiguration.migrateLegacyDefaults(previousDefault));

        HashSet<String> custom = new HashSet<>(Collections.singletonList(
                ShortcutRailConfiguration.COPY));
        assertEquals(custom, ShortcutRailConfiguration.migrateLegacyDefaults(custom));
    }

    @Test
    public void unknownActionsAreIgnored() {
        assertTrue(ShortcutRailConfiguration.orderedSelection(
                new HashSet<>(Collections.singletonList("unknown"))).isEmpty());
    }
}
