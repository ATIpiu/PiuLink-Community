package com.limelight.utils;

import android.app.Activity;
import android.content.SharedPreferences;
import android.preference.PreferenceManager;
import android.widget.CheckBox;
import android.widget.FrameLayout;
import com.limelight.R;

/** Settings for optional messages. Topic IDs are resource names, independent of locale or peer. */
public final class ReminderPreferences {
    private static final String MASTER = "show_reminder_dialogs";
    private static final int[] TOPICS = {
            R.string.conn_error_title, R.string.conn_terminated_title, R.string.title_native_res_dialog,
            R.string.title_native_fps_dialog, R.string.title_decoding_error, R.string.title_decoding_reset,
            R.string.reminder_screen, R.string.reminder_ssh, R.string.reminder_microphone,
            R.string.invite_vpn_choice_title
    };

    private ReminderPreferences() {}
    private static SharedPreferences prefs(Activity a) { return PreferenceManager.getDefaultSharedPreferences(a); }
    private static String key(Activity a, int topic) { return "reminder.show." + a.getResources().getResourceEntryName(topic); }

    private static boolean enabled(Activity a, int topic) {
        String name = a.getResources().getResourceEntryName(topic);
        SharedPreferences legacy = a.getSharedPreferences("piulink_suppressed_dialogs", Activity.MODE_PRIVATE);
        boolean previousVpnChoice = topic == R.string.invite_vpn_choice_title &&
                prefs(a).getBoolean("invite_vpn_route_choice_made", false);
        return prefs(a).getBoolean(key(a, topic), !previousVpnChoice && !legacy.getBoolean(name, false)
                && !legacy.getBoolean("dialog_" + Integer.toHexString(a.getString(topic).hashCode()), false));
    }

    static void migrateNotice(Activity a, int topic, String oldKey) {
        if (!prefs(a).contains(key(a, topic)) &&
                a.getSharedPreferences("piulink_suppressed_dialogs", Activity.MODE_PRIVATE).getBoolean(oldKey, false)) {
            prefs(a).edit().putBoolean(key(a, topic), false).apply();
        }
    }

    public static boolean shouldShow(Activity a, int topic) {
        return prefs(a).getBoolean(MASTER, true) && enabled(a, topic);
    }

    public static CheckBox addCheckbox(Activity a, TouchDialog builder) {
        CheckBox remember = new CheckBox(builder.getContext());
        remember.setText(R.string.dialog_dont_show_again);
        FrameLayout area = new FrameLayout(a);
        area.setPadding(TouchDialog.dp(a, 20), 0, TouchDialog.dp(a, 16), 0);
        area.addView(remember);
        builder.setView(area);
        return remember;
    }

    public static void remember(Activity a, int topic, CheckBox choice) {
        if (choice.isChecked()) prefs(a).edit().putBoolean(key(a, topic), false).apply();
    }

    public static void offerAction(Activity a, int topic, int title, int message, int actionLabel, Runnable action) {
        if (!shouldShow(a, topic)) return;
        TouchDialog builder = new TouchDialog(a).setTitle(title);
        CheckBox choice = addCheckbox(a, builder);
        android.app.AlertDialog dialog = builder.setMessage(message)
                .setPositiveButton(actionLabel, (d, w) -> action.run())
                .setNegativeButton(android.R.string.cancel, null).create();
        dialog.setOnDismissListener(d -> remember(a, topic, choice));
        dialog.show();
    }

    public static void manage(Activity a) {
        CharSequence[] labels = new CharSequence[TOPICS.length];
        boolean[] shown = new boolean[TOPICS.length];
        for (int i = 0; i < TOPICS.length; i++) {
            labels[i] = a.getText(TOPICS[i]);
            shown[i] = enabled(a, TOPICS[i]);
        }
        new TouchDialog(a).setTitle(R.string.manage_reminders)
                .setMultiChoiceItems(labels, shown, (d, which, checked) -> shown[which] = checked)
                .setPositiveButton(android.R.string.ok, (d, w) -> {
                    SharedPreferences.Editor changes = prefs(a).edit();
                    for (int i = 0; i < TOPICS.length; i++) changes.putBoolean(key(a, TOPICS[i]), shown[i]);
                    changes.apply();
                }).setNegativeButton(android.R.string.cancel, null)
                .setNeutralButton(R.string.title_reset_suppressed_dialogs, (d, w) -> {
                    SharedPreferences.Editor changes = prefs(a).edit();
                    for (int topic : TOPICS) changes.putBoolean(key(a, topic), true);
                    changes.apply();
                    a.getSharedPreferences("piulink_suppressed_dialogs", Activity.MODE_PRIVATE).edit().clear().apply();
                }).show();
    }
}
