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
    private final int topic;
    private final boolean errorNotice;
    private static String noticeKey(String title, String message) {
        return "notice/" + UUID.nameUUIDFromBytes((title + "\n" + message).getBytes(StandardCharsets.UTF_8));
    }

    private final String title;
    private final String message;
    private final Activity activity;
    private final Runnable runOnDismiss;

    private AlertDialog alert;

    private static final ArrayList<Dialog> rundownDialogs = new ArrayList<>();

    private Dialog(Activity activity, String title, String message, Runnable runOnDismiss, int topic, boolean errorNotice)
    {
        this.topic = topic;
        this.errorNotice = errorNotice;
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

    public static void displayDialog(final Activity activity, int title, String message, final boolean endAfterDismiss)
    {
        activity.runOnUiThread(new Dialog(activity, activity.getString(title), message, new Runnable() {
            @Override
            public void run() {
                if (endAfterDismiss) {
                    activity.finish();
                }
            }
        }, title, true));
    }

    public static void displayDialog(Activity activity, int title, String message, Runnable runOnDismiss)
    {
        activity.runOnUiThread(new Dialog(activity, activity.getString(title), message, runOnDismiss, title, true));
    }

    public static void displayResult(Activity activity, String title, String message) {
        activity.runOnUiThread(new Dialog(activity, title, message, () -> { }, 0, false));
    }

    public static void displayTip(Activity activity, int title, String message) {
        activity.runOnUiThread(new Dialog(activity, activity.getString(title), message, () -> {}, title, false));
    }

    public static void displayTip(Activity activity, int title, int message, Runnable onDismiss) {
        displayTip(activity, title, title, message, onDismiss);
    }

    public static void displayTip(Activity activity, int topic, int title, int message, Runnable onDismiss) {
        activity.runOnUiThread(new Dialog(activity, activity.getString(title), activity.getString(message),
                onDismiss, topic, false));
    }

    @Override
    public void run() {
        if (activity.isFinishing() || activity.isDestroyed()) return;
        if (errorNotice) ReminderPreferences.migrateNotice(activity, topic, noticeKey(title, message));
        if (topic != 0 && !ReminderPreferences.shouldShow(activity, topic)) {
            if (errorNotice) Toast.makeText(activity, message, Toast.LENGTH_LONG).show();
            runOnDismiss.run();
            return;
        }

        TouchDialog builder = new TouchDialog(activity).setTitle(title);
        builder.setMessage(message);
        CheckBox remember = topic == 0 ? null : ReminderPreferences.addCheckbox(activity, builder);
        Runnable acknowledge = () -> {
            synchronized (rundownDialogs) {
                if (!rundownDialogs.remove(this)) return;
            }
            if (remember != null) ReminderPreferences.remember(activity, topic, remember);
            runOnDismiss.run();
        };
        builder.setPositiveButton(android.R.string.ok, (d, which) -> acknowledge.run());
        if (topic == 0 || errorNotice) builder.setNeutralButton(R.string.help, (d, which) -> {
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

}
