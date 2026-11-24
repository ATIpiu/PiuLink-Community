package com.limelight.piulink;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

public final class ShortcutRailConfiguration {
    public static final String WINDOW_NEXT = "window_next";
    public static final String WINDOW_PREVIOUS = "window_previous";
    public static final String TASK_VIEW = "task_view";
    public static final String SHOW_DESKTOP = "show_desktop";
    public static final String CLOSE_WINDOW = "close_window";
    public static final String TASK_MANAGER = "task_manager";
    public static final String COPY = "copy";
    public static final String PASTE = "paste";
    public static final String UNDO = "undo";
    public static final String ESCAPE = "escape";
    public static final String KEYBOARD = "keyboard";
    public static final String VOICE_INPUT = "voice_input";

    private static final List<String> ORDER = Collections.unmodifiableList(Arrays.asList(
            WINDOW_NEXT,
            WINDOW_PREVIOUS,
            TASK_VIEW,
            SHOW_DESKTOP,
            CLOSE_WINDOW,
            TASK_MANAGER,
            COPY,
            PASTE,
            UNDO,
            ESCAPE,
            KEYBOARD,
            VOICE_INPUT));

    private static final Set<String> DEFAULTS = Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
            WINDOW_NEXT,
            WINDOW_PREVIOUS,
            TASK_VIEW,
            SHOW_DESKTOP,
            CLOSE_WINDOW,
            TASK_MANAGER,
            COPY,
            PASTE,
            ESCAPE,
            KEYBOARD,
            VOICE_INPUT)));
    private static final Set<String> DEFAULTS_WITHOUT_VOICE_INPUT =
            Collections.unmodifiableSet(new HashSet<>(Arrays.asList(
                    WINDOW_NEXT,
                    WINDOW_PREVIOUS,
                    TASK_VIEW,
                    SHOW_DESKTOP,
                    CLOSE_WINDOW,
                    TASK_MANAGER,
                    COPY,
                    PASTE,
                    ESCAPE,
                    KEYBOARD)));
    private static final Set<String> LEGACY_DEFAULTS = Collections.unmodifiableSet(
            new HashSet<>(Arrays.asList(WINDOW_NEXT, TASK_VIEW, SHOW_DESKTOP, KEYBOARD)));

    private ShortcutRailConfiguration() {
    }

    public static Set<String> defaults() {
        return new HashSet<>(DEFAULTS);
    }

    /** Expands prior shipped defaults without overwriting a user's custom selection. */
    public static Set<String> migrateLegacyDefaults(Set<String> selected) {
        if (selected != null && (selected.equals(LEGACY_DEFAULTS) ||
                selected.equals(DEFAULTS_WITHOUT_VOICE_INPUT))) return defaults();
        return selected == null ? defaults() : new HashSet<>(selected);
    }

    public static List<String> orderedSelection(Set<String> selected) {
        if (selected == null || selected.isEmpty()) {
            return Collections.emptyList();
        }

        List<String> result = new ArrayList<>();
        for (String id : ORDER) {
            if (selected.contains(id)) {
                result.add(id);
            }
        }
        return result;
    }
}
