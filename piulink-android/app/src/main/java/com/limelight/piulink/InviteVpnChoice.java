package com.limelight.piulink;

import com.limelight.utils.TouchDialog;
import com.limelight.utils.ReminderPreferences;
import android.widget.CheckBox;

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
        if (!NetHelper.isActiveNetworkVpn(activity) || NetHelper.preferPhysicalInviteNetwork(activity) ||
                !ReminderPreferences.shouldShow(activity, R.string.invite_vpn_choice_title)) return false;
        TouchDialog builder = new TouchDialog(activity).setTitle(R.string.invite_vpn_choice_title);
        CheckBox choice = ReminderPreferences.addCheckbox(activity, builder);
        AlertDialog dialog = builder.setMessage(R.string.invite_vpn_choice_help)
                .setPositiveButton(R.string.invite_vpn_choose_direct, (d, w) -> save(activity, true, connect, false))
                .setNegativeButton(R.string.invite_vpn_keep_system, (d, w) -> save(activity, false, connect, false)).create();
        dialog.setOnDismissListener(d -> ReminderPreferences.remember(activity, R.string.invite_vpn_choice_title, choice));
        dialog.show();
        return true;
    }

    public static void noteChoice(Context context) {
        PreferenceManager.getDefaultSharedPreferences(context).edit().putBoolean(CHOICE_MADE, true).apply();
    }

    public static void choose(Activity activity, Runnable onChosen) {
        new TouchDialog(activity)
                .setTitle(R.string.invite_vpn_choice_title)
                .setMessage(R.string.invite_vpn_choice_help)
                .setPositiveButton(R.string.invite_vpn_choose_direct, (d, w) -> save(activity, true, onChosen, true))
                .setNegativeButton(R.string.invite_vpn_keep_system, (d, w) -> save(activity, false, onChosen, true))
                .show();
    }

    private static void save(Activity activity, boolean direct, Runnable onChosen, boolean explicitSetting) {
        PreferenceManager.getDefaultSharedPreferences(activity).edit()
                .putBoolean(NetHelper.DIRECT_INVITE_NETWORK, direct).putBoolean(CHOICE_MADE, explicitSetting).apply();
        onChosen.run();
    }
}
