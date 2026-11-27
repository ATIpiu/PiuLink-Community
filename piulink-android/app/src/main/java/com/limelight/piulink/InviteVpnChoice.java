package com.limelight.piulink;

import com.limelight.utils.TouchDialog;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.content.Context;
import android.preference.PreferenceManager;

import com.limelight.R;
import com.limelight.utils.NetHelper;

public final class InviteVpnChoice {
    private static final String CHOICE_MADE = "invite_vpn_route_choice_made";

    private InviteVpnChoice() {}

    public static boolean offer(Activity activity, Runnable connect) {
        SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(activity);
        if (!NetHelper.isActiveNetworkVpn(activity) || NetHelper.preferPhysicalInviteNetwork(activity) ||
                prefs.getBoolean(CHOICE_MADE, false)) return false;
        choose(activity, connect);
        return true;
    }

    public static void noteChoice(Context context) {
        PreferenceManager.getDefaultSharedPreferences(context).edit().putBoolean(CHOICE_MADE, true).apply();
    }

    public static void choose(Activity activity, Runnable onChosen) {
        new TouchDialog(activity)
                .setTitle(R.string.invite_vpn_choice_title)
                .setMessage(R.string.invite_vpn_choice_help)
                .setPositiveButton(R.string.invite_vpn_choose_direct, (d, w) -> save(activity, true, onChosen))
                .setNegativeButton(R.string.invite_vpn_keep_system, (d, w) -> save(activity, false, onChosen))
                .show();
    }

    private static void save(Activity activity, boolean direct, Runnable onChosen) {
        PreferenceManager.getDefaultSharedPreferences(activity).edit()
                .putBoolean(NetHelper.DIRECT_INVITE_NETWORK, direct).putBoolean(CHOICE_MADE, true).apply();
        onChosen.run();
    }
}
