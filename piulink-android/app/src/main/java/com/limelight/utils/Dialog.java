package com.limelight.utils;

import java.util.ArrayList;

import android.app.Activity;
import android.app.AlertDialog;
import java.util.UUID;
import java.nio.charset.StandardCharsets;
import android.widget.Toast;
import android.widget.CheckBox;

import com.limelight.R;

public class Dialog implements Runnable {
    private static final String SUPPRESSION_PREFERENCES = "piulink_suppressed_dialogs";
    private final String tipKey;
    private static String noticeKey(String title, String message) {
        return "notice/" + UUID.nameUUIDFromBytes((title + "\n" + message).getBytes(StandardCharsets.UTF_8));
    }

    private final String title;
    private final String message;
    private final Activity activity;
    private final Runnable runOnDismiss;

    private AlertDialog alert;

    private static final ArrayList<Dialog> rundownDialogs = new ArrayList<>();

    private Dialog(Activity activity, String title, String message, Runnable runOnDismiss, String tipKey)
    {
        this.tipKey = tipKey;
        this.activity = activity;
        this.title = title;
        this.message = message;
        this.runOnDismiss = runOnDismiss;
    }

    public static void closeDialogs(Activity owner)
    {
        synchronized (rundownDialogs) {
            java.util.Iterator<Dialog> iterator = rundownDialogs.iterator();
            while (iterator.hasNext()) {
                Dialog dialog = iterator.next();
                if (dialog.activity != owner) continue;
                iterator.remove();
                if (dialog.alert.isShowing()) dialog.alert.dismiss();
            }
        }
    }

    public static void displayDialog(final Activity activity, String title, String message, final boolean endAfterDismiss)
    {
        activity.runOnUiThread(new Dialog(activity, title, message, new Runnable() {
            @Override
            public void run() {
                if (endAfterDismiss) {
                    activity.finish();
                }
            }
        }, noticeKey(title, message)));
    }

    public static void displayDialog(Activity activity, String title, String message, Runnable runOnDismiss)
    {
        activity.runOnUiThread(new Dialog(activity, title, message, runOnDismiss, noticeKey(title, message)));
    }

    public static void displayResult(Activity activity, String title, String message) {
        activity.runOnUiThread(new Dialog(activity, title, message, () -> { }, null));
    }

    public static void displayTip(Activity activity, int title, int message, Runnable onDismiss) {
        String key = activity.getResources().getResourceEntryName(title);
        activity.runOnUiThread(new Dialog(activity, activity.getString(title), activity.getString(message),
                onDismiss, key));
    }

    @Override
    public void run() {
        if (activity.isFinishing() || activity.isDestroyed()) return;
        android.content.SharedPreferences prefs = activity.getSharedPreferences(SUPPRESSION_PREFERENCES, Activity.MODE_PRIVATE);
        if (tipKey != null) {
            String legacy = "dialog_" + Integer.toHexString(title.hashCode());
            if (!tipKey.startsWith("notice/") && prefs.getBoolean(legacy, false)) prefs.edit().remove(legacy).putBoolean(tipKey, true).apply();
            if (prefs.getBoolean(tipKey, false)) {
                if (tipKey.startsWith("notice/")) Toast.makeText(activity, message, Toast.LENGTH_LONG).show();
                runOnDismiss.run();
                return;
            }
        }

        TouchDialog builder = new TouchDialog(activity).setTitle(title);
        builder.setMessage(message);
        CheckBox remember = tipKey == null ? null : new CheckBox(activity);
        if (remember != null) {
            remember.setText(R.string.dialog_dont_show_again);
            android.widget.FrameLayout area = new android.widget.FrameLayout(activity);
            area.setPadding(TouchDialog.dp(activity, 20), 0, TouchDialog.dp(activity, 16), 0);
            area.addView(remember);
            builder.setView(area);
        }
        Runnable acknowledge = () -> {
            synchronized (rundownDialogs) {
                if (!rundownDialogs.remove(this)) return;
            }
            if (remember != null && remember.isChecked()) prefs.edit().putBoolean(tipKey, true).apply();
            runOnDismiss.run();
        };
        builder.setPositiveButton(android.R.string.ok, (d, which) -> acknowledge.run());
        if (tipKey == null || tipKey.startsWith("notice/")) builder.setNeutralButton(R.string.help, (d, which) -> {
            acknowledge.run();
            HelpLauncher.launchTroubleshooting(activity);
        });
        builder.setOnCancelListener(d -> acknowledge.run());
        alert = builder.create();
        synchronized (rundownDialogs) {
            rundownDialogs.add(this);
            alert.show();
        }
    }

    public static void resetSuppressedDialogs(Activity activity) {
        activity.getSharedPreferences(SUPPRESSION_PREFERENCES, Activity.MODE_PRIVATE)
                .edit().clear().apply();
    }

}
