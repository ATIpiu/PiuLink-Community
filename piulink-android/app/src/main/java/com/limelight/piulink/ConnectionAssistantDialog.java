package com.limelight.piulink;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.res.Resources;
import android.view.LayoutInflater;
import android.view.View;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.utils.NetHelper;
import com.limelight.utils.TailscaleLauncher;

/**
 * Presents host readiness as a small set of concrete recovery actions.
 * This intentionally wraps the existing Moonlight operations instead of
 * duplicating networking or pairing logic.
 */
public final class ConnectionAssistantDialog {
    public interface Listener {
        void onRetry();
        void onWake();
        void onPair();
        void onOpenLibrary();
        void onOpenPrivateNetwork();
        void onNetworkTest();
    }

    private ConnectionAssistantDialog() {
    }

    public static AlertDialog show(final Activity activity,
                                   final ComputerDetails computer,
                                   final Listener listener) {
        View content = LayoutInflater.from(activity)
                .inflate(R.layout.dialog_connection_assistant, null, false);

        TextView title = content.findViewById(R.id.assistantTitle);
        TextView summary = content.findViewById(R.id.assistantSummary);
        TextView hostStatus = content.findViewById(R.id.assistantHostStatus);
        TextView pairingStatus = content.findViewById(R.id.assistantPairingStatus);
        TextView networkStatus = content.findViewById(R.id.assistantNetworkStatus);
        View hostDot = content.findViewById(R.id.assistantHostDot);
        View pairingDot = content.findViewById(R.id.assistantPairingDot);
        View networkDot = content.findViewById(R.id.assistantNetworkDot);
        TextView primaryButton = content.findViewById(R.id.assistantPrimaryButton);
        TextView retryButton = content.findViewById(R.id.assistantRetryButton);
        TextView privateNetworkButton = content.findViewById(R.id.assistantPrivateNetworkButton);
        TextView networkTestButton = content.findViewById(R.id.assistantNetworkTestButton);
        TextView closeButton = content.findViewById(R.id.assistantCloseButton);

        title.setText(activity.getString(R.string.connection_assistant_title, computer.name));

        final boolean online = computer.state == ComputerDetails.State.ONLINE;
        final boolean refreshing = computer.state == ComputerDetails.State.UNKNOWN;
        final boolean paired = computer.pairState == PairingManager.PairState.PAIRED;
        final boolean privateNetworkActive = NetHelper.isActiveNetworkVpn(activity);
        final boolean tailscaleInstalled = TailscaleLauncher.isInstalled(activity);

        if (online) {
            setStatus(activity, hostStatus, hostDot,
                    R.string.connection_host_online, R.color.piulink_success,
                    R.drawable.bg_status_ready);
        }
        else if (refreshing) {
            setStatus(activity, hostStatus, hostDot,
                    R.string.connection_host_refreshing, R.color.piulink_warning,
                    R.drawable.bg_status_attention);
        }
        else {
            setStatus(activity, hostStatus, hostDot,
                    R.string.connection_host_offline, R.color.piulink_danger,
                    R.drawable.bg_status_error);
        }

        if (paired) {
            setStatus(activity, pairingStatus, pairingDot,
                    R.string.connection_pairing_ready, R.color.piulink_success,
                    R.drawable.bg_status_ready);
        }
        else {
            setStatus(activity, pairingStatus, pairingDot,
                    R.string.connection_pairing_required, R.color.piulink_warning,
                    R.drawable.bg_status_attention);
        }

        if (privateNetworkActive) {
            setStatus(activity, networkStatus, networkDot,
                    R.string.connection_private_network_active, R.color.piulink_success,
                    R.drawable.bg_status_ready);
        }
        else if (tailscaleInstalled) {
            setStatus(activity, networkStatus, networkDot,
                    R.string.connection_private_network_available, R.color.piulink_warning,
                    R.drawable.bg_status_attention);
        }
        else {
            setStatus(activity, networkStatus, networkDot,
                    R.string.connection_private_network_optional, R.color.piulink_text_secondary,
                    R.drawable.bg_status_neutral);
        }

        final AlertDialog dialog = new AlertDialog.Builder(activity)
                .setView(content)
                .create();
        dialog.setCanceledOnTouchOutside(true);

        if (online && paired) {
            summary.setText(R.string.connection_ready_summary);
            primaryButton.setText(R.string.open_library);
            primaryButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
                @Override
                public void run() {
                    listener.onOpenLibrary();
                }
            }));
        }
        else if (online) {
            summary.setText(R.string.connection_pair_summary);
            primaryButton.setText(R.string.pcview_menu_pair_pc);
            primaryButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
                @Override
                public void run() {
                    listener.onPair();
                }
            }));
        }
        else if (!refreshing && computer.macAddress != null) {
            summary.setText(R.string.connection_wake_summary);
            primaryButton.setText(R.string.pcview_menu_send_wol);
            primaryButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
                @Override
                public void run() {
                    listener.onWake();
                }
            }));
        }
        else {
            summary.setText(refreshing
                    ? R.string.connection_refreshing_summary
                    : R.string.connection_offline_summary);
            primaryButton.setText(R.string.retry_connection);
            primaryButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
                @Override
                public void run() {
                    listener.onRetry();
                }
            }));
            retryButton.setVisibility(View.GONE);
        }

        retryButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
            @Override
            public void run() {
                listener.onRetry();
            }
        }));
        privateNetworkButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
            @Override
            public void run() {
                listener.onOpenPrivateNetwork();
            }
        }));
        networkTestButton.setOnClickListener(new DismissAction(dialog, new Runnable() {
            @Override
            public void run() {
                listener.onNetworkTest();
            }
        }));
        closeButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                dialog.dismiss();
            }
        });

        dialog.setOnShowListener(new DialogInterface.OnShowListener() {
            @Override
            public void onShow(DialogInterface dialogInterface) {
                primaryButton.requestFocus();
            }
        });
        dialog.show();
        return dialog;
    }

    private static void setStatus(Activity activity, TextView label, View dot,
                                  int textResource, int colorResource,
                                  int dotBackgroundResource) {
        Resources resources = activity.getResources();
        label.setText(textResource);
        label.setTextColor(android.os.Build.VERSION.SDK_INT >= 23
                ? resources.getColor(colorResource, activity.getTheme()) : resources.getColor(colorResource));
        dot.setBackgroundResource(dotBackgroundResource);
    }

    private static final class DismissAction implements View.OnClickListener {
        private final AlertDialog dialog;
        private final Runnable action;

        DismissAction(AlertDialog dialog, Runnable action) {
            this.dialog = dialog;
            this.action = action;
        }

        @Override
        public void onClick(View view) {
            dialog.dismiss();
            action.run();
        }
    }
}
