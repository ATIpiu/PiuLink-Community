package com.limelight.utils;

import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.widget.Toast;

import com.limelight.R;

public final class TailscaleLauncher {
    public static final String PACKAGE_NAME = "com.tailscale.ipn";
    public static final String GOOGLE_PLAY_URL =
            "https://play.google.com/store/apps/details?id=" + PACKAGE_NAME;

    private TailscaleLauncher() {
    }

    public static boolean isInstalled(Context context) {
        return context.getPackageManager().getLaunchIntentForPackage(PACKAGE_NAME) != null;
    }

    public static void openAppOrPlayStore(Context context) {
        Intent launchIntent = context.getPackageManager().getLaunchIntentForPackage(PACKAGE_NAME);
        if (launchIntent != null) {
            launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(launchIntent);
            return;
        }

        Toast.makeText(context, R.string.tailscale_not_installed, Toast.LENGTH_LONG).show();
        try {
            Intent playStoreIntent = new Intent(
                    Intent.ACTION_VIEW,
                    Uri.parse("market://details?id=" + PACKAGE_NAME));
            playStoreIntent.setPackage("com.android.vending");
            playStoreIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(playStoreIntent);
        }
        catch (ActivityNotFoundException ignored) {
            HelpLauncher.launchUrl(context, GOOGLE_PLAY_URL);
        }
    }
}
