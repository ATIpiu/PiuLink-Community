package com.limelight.preferences;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Typeface;
import android.text.InputFilter;
import android.text.format.DateUtils;
import android.view.Gravity;
import android.view.View;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.ScrollView;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.computers.ComputerManagerService.ComputerManagerBinder;
import com.limelight.computers.ConnectionHistory;
import com.limelight.computers.HostInvite;
import com.limelight.utils.NetHelper;

import java.net.URI;
import java.util.function.BiConsumer;

public final class ConnectionRoutePicker {
    private final Activity activity;
    private final ComputerManagerBinder manager;
    private final String hostId;
    private final BiConsumer<ConnectionHistory, ConnectionHistory.Route> connect;
    private final LinearLayout rows;
    private final AlertDialog dialog;

    public static void show(Activity activity, ComputerManagerBinder manager, String hostId,
                            BiConsumer<ConnectionHistory, ConnectionHistory.Route> connect) {
        show(activity, manager, hostId, hostId == null ? R.string.connection_history : R.string.connection_methods, connect);
    }

    public static void show(Activity activity, ComputerManagerBinder manager, String hostId, int title,
                            BiConsumer<ConnectionHistory, ConnectionHistory.Route> connect) {
        ConnectionRoutePicker picker = new ConnectionRoutePicker(activity, manager, hostId, title, connect);
        picker.render();
        picker.dialog.show();
        if (picker.dialog.getWindow() != null) {
            picker.dialog.getWindow().setBackgroundDrawableResource(R.drawable.bg_display_window);
            int available = activity.getResources().getDisplayMetrics().widthPixels;
            picker.dialog.getWindow().setLayout(Math.min(available - picker.dp(24), picker.dp(480)), -2);
        }
    }

    private ConnectionRoutePicker(Activity activity, ComputerManagerBinder manager, String hostId, int title,
                                  BiConsumer<ConnectionHistory, ConnectionHistory.Route> connect) {
        this.activity = activity;
        this.manager = manager;
        this.hostId = hostId;
        this.connect = connect;
        rows = new LinearLayout(activity);
        rows.setOrientation(LinearLayout.VERTICAL);
        rows.setPadding(dp(16), dp(6), dp(16), dp(8));
        ScrollView scroll = new ScrollView(activity);
        scroll.addView(rows);
        dialog = new AlertDialog.Builder(activity)
                .setTitle(title)
                .setView(scroll).setNegativeButton(android.R.string.cancel, null).create();
    }

    private int dp(int value) {
        return Math.round(activity.getResources().getDisplayMetrics().density * value);
    }

    private TextView text(String message, int sp, int color) {
        TextView view = new TextView(activity);
        view.setText(message);
        view.setTextSize(sp);
        view.setTextColor(activity.getColor(color));
        return view;
    }

    private void render() {
        rows.removeAllViews();
        for (ConnectionHistory host : manager.connectionHistory()) {
            if (hostId != null && !hostId.equalsIgnoreCase(host.uuid)) continue;
            String name = host.name.equalsIgnoreCase(host.uuid) ?
                    activity.getString(R.string.history_unknown_host, host.uuid.substring(0, Math.min(8, host.uuid.length()))) : host.name;
            TextView heading = text(name, 14, R.color.home_muted);
            heading.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
            heading.setPadding(dp(4), dp(10), 0, dp(6));
            if (android.os.Build.VERSION.SDK_INT >= 28) heading.setAccessibilityHeading(true);
            rows.addView(heading);
            int invites = 0;
            for (ConnectionHistory.Route route : host.routes) if (HostInvite.parseUri(route.value) != null) invites++;
            for (ConnectionHistory.Route route : host.routes) addRoute(host, route, invites > 1);
        }
        if (rows.getChildCount() == 0) rows.addView(text(activity.getString(R.string.history_empty), 15, R.color.home_muted));
    }

    private void addRoute(ConnectionHistory host, ConnectionHistory.Route route, boolean severalInvites) {
        URI invite = HostInvite.parseUri(route.value);
        String kind = activity.getString(invite == null ? addressKind(route.value) : R.string.route_invitation);
        String detail = invite == null ? route.value : invite.getRawAuthority();
        if (invite != null && severalInvites) detail += " · " + route.key.substring(0, 6);
        String title = route.label.isEmpty() ? kind : route.label;
        if (!route.label.isEmpty()) detail = kind + " · " + detail;
        LinearLayout row = new LinearLayout(activity);
        row.setBackgroundResource(R.drawable.bg_home_library);
        row.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1,-2);
        params.topMargin = dp(5);
        rows.addView(row, params);
        LinearLayout main = new LinearLayout(activity);
        main.setOrientation(LinearLayout.VERTICAL);
        main.setPadding(dp(14), dp(10), dp(4), dp(10));
        main.setMinimumHeight(dp(56));
        main.setGravity(Gravity.CENTER_VERTICAL);
        main.setFocusable(true);
        main.setOnClickListener(v -> { dialog.dismiss(); connect.accept(host, route); });
        TextView label = text(title, 16, R.color.home_ink);
        label.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        main.addView(label);
        main.addView(text(detail, 13, R.color.home_muted));
        if (route.lastUsed > 0) {
            CharSequence time = DateUtils.getRelativeTimeSpanString(route.lastUsed, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS);
            main.addView(text(activity.getString(R.string.route_last_used, time), 12, R.color.home_muted));
        }
        row.addView(main, new LinearLayout.LayoutParams(0,-2,1));
        TextView more = text("⋮", 24, R.color.home_ink);
        more.setGravity(Gravity.CENTER);
        more.setContentDescription(activity.getString(R.string.route_manage_named, title));
        more.setFocusable(true);
        more.setOnClickListener(v -> menu(v, host, route, title));
        row.addView(more, new LinearLayout.LayoutParams(dp(48), dp(48)));
    }

    private int addressKind(String value) {
        URI uri = AddComputerManually.parseRawUserInputToUri(value);
        if (uri == null || uri.getHost() == null) return R.string.route_address;
        String host = uri.getHost().replace("[","").replace("]","").toLowerCase(java.util.Locale.ROOT);
        if (host.endsWith(".ts.net") || host.startsWith("fd7a:115c:a1e0:")) return R.string.route_tailnet;
        if (host.matches("100\\.(?:6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\\..*")) return R.string.route_shared;
        if (!host.contains(":") && !host.matches("[0-9.]+")) return R.string.route_hostname;
        return NetHelper.isPrivateHostAddress(host) ? R.string.route_lan : R.string.route_wan;
    }

    private void menu(View anchor, ConnectionHistory host, ConnectionHistory.Route route, String title) {
        PopupMenu menu = new PopupMenu(activity, anchor);
        menu.getMenu().add(R.string.route_rename).setOnMenuItemClickListener(item -> {
            EditText input = new EditText(activity);
            input.setSingleLine(true);
            input.setFilters(new InputFilter[]{new InputFilter.LengthFilter(64)});
            input.setText(route.label);
            input.setSelectAllOnFocus(true);
            input.setHint(title);
            LinearLayout area = new LinearLayout(activity);
            area.setPadding(dp(20), dp(6), dp(20), 0);
            area.addView(input, new LinearLayout.LayoutParams(-1,-2));
            new AlertDialog.Builder(activity).setTitle(R.string.route_rename).setView(area)
                    .setNegativeButton(android.R.string.cancel,null).setPositiveButton(android.R.string.ok,(d,w)->{
                        manager.renameRoute(host.uuid, route.value, input.getText().toString());
                        render();
                    }).show();
            return true;
        });
        menu.getMenu().add(R.string.route_remove).setOnMenuItemClickListener(item -> {
            new AlertDialog.Builder(activity).setMessage(activity.getString(R.string.route_remove_prompt, title))
                    .setNegativeButton(android.R.string.cancel,null).setPositiveButton(R.string.route_remove,(d,w)->{
                        manager.removeRoute(host.uuid,route.value);
                        render();
                    }).show();
            return true;
        });
        menu.show();
    }
}
