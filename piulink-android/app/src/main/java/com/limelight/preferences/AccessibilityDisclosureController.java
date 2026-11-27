package com.limelight.preferences;

import com.limelight.utils.TouchDialog;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.DialogInterface;
import android.content.SharedPreferences;
import android.view.View;

import com.limelight.R;

/**
 * Shows the prominent disclosure required before asking the user to enable the
 * optional keyboard-shortcut AccessibilityService.
 *
 * <p>The acceptance record is deliberately kept in app-private preferences. It
 * is not included in Android backup, so a fresh installation must disclose the
 * behavior and obtain consent again.</p>
 */
public final class AccessibilityDisclosureController {
    private static final String PREFS_NAME = "accessibility_disclosure";
    private static final String PREF_ACCEPTED_VERSION = "accepted_version";
    private static final int DISCLOSURE_VERSION = 1;

    public interface Callback {
        void onAccepted();

        void onDeclined();
    }

    private AccessibilityDisclosureController() {
    }

    public static boolean hasRecordedConsent(Context context) {
        return getPreferences(context).getInt(PREF_ACCEPTED_VERSION, 0)
                >= DISCLOSURE_VERSION;
    }

    /**
     * Runs {@link Callback#onAccepted()} immediately when the current disclosure
     * was already accepted. Otherwise, displays a non-dismissible-by-touch dialog
     * and records consent only after an explicit positive action.
     */
    public static void showIfNeeded(Activity activity, Callback callback) {
        if (activity == null || activity.isFinishing() || activity.isDestroyed()) {
            callback.onDeclined();
            return;
        }

        if (hasRecordedConsent(activity)) {
            callback.onAccepted();
            return;
        }

        View disclosureView = activity.getLayoutInflater().inflate(
                R.layout.dialog_accessibility_disclosure, null, false);
        final boolean[] resolved = {false};

        AlertDialog dialog = new TouchDialog(activity)
                .setTitle(R.string.accessibility_disclosure_title)
                .setView(disclosureView)
                .setPositiveButton(R.string.accessibility_disclosure_continue, (ignored, which) -> {
                    if (!resolved[0]) {
                        resolved[0] = true;
                        recordConsent(activity);
                        callback.onAccepted();
                    }
                })
                .setNegativeButton(R.string.accessibility_disclosure_not_now, (ignored, which) -> {
                    if (!resolved[0]) {
                        resolved[0] = true;
                        callback.onDeclined();
                    }
                })
                .create();

        dialog.setCanceledOnTouchOutside(false);
        dialog.setOnCancelListener(ignored -> {
            if (!resolved[0]) {
                resolved[0] = true;
                callback.onDeclined();
            }
        });
        dialog.show();
        dialog.getButton(DialogInterface.BUTTON_POSITIVE).setFilterTouchesWhenObscured(true);
        dialog.getButton(DialogInterface.BUTTON_NEGATIVE).setFilterTouchesWhenObscured(true);
    }

    private static void recordConsent(Context context) {
        getPreferences(context).edit()
                .putInt(PREF_ACCEPTED_VERSION, DISCLOSURE_VERSION)
                .apply();
    }

    private static SharedPreferences getPreferences(Context context) {
        return context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE);
    }
}
