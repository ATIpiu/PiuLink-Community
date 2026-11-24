package com.limelight.piulink.ssh;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.widget.*;
import com.limelight.R;

/** A visible OpenSSH terminal entry, independent of Sunshine discovery/pairing. */
public final class TerminalPanel extends LinearLayout {
    private final Activity activity;
    private LinearLayout list;
    public TerminalPanel(Activity activity, String suggestedHost) {
        super(activity);
        this.activity = activity;
        setOrientation(VERTICAL);

        LinearLayout root = new LinearLayout(activity); root.setOrientation(LinearLayout.VERTICAL);
        int padding = Math.round(16 * getResources().getDisplayMetrics().density);
        root.setPadding(padding, padding, padding, padding);
        TextView title = new TextView(activity);
        title.setText(R.string.ssh_title);
        title.setTextSize(28);
        title.setTextColor(getResources().getColor(R.color.piulink_text_primary));
        root.addView(title);
        TextView detail = new TextView(activity);
        detail.setText(R.string.ssh_intro);
        detail.setTextColor(getResources().getColor(R.color.piulink_text_secondary));
        detail.setPadding(0, padding / 3, 0, padding);
        root.addView(detail);
        Button add = new Button(activity);
        add.setText(R.string.ssh_add);
        add.setAllCaps(false);
        add.setBackgroundResource(R.drawable.bg_dialog_primary);
        add.setTextColor(getResources().getColor(R.color.piulink_on_accent));
        add.setOnClickListener(v -> addProfile(null));
        root.addView(add);
        ScrollView scroll = new ScrollView(activity); list = new LinearLayout(activity); list.setOrientation(LinearLayout.VERTICAL);
        scroll.addView(list); root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1)); addView(root, new LinearLayout.LayoutParams(-1, -1));


        if (suggestedHost != null) {
            String host = suggestedHost;
            java.util.List<SshProfile> matches = new java.util.ArrayList<>();
            java.util.List<SshProfile> profiles;
            try { profiles = SshProfile.load(activity); }
            catch (IllegalStateException e) { return; } // onResume renders the storage error.
            for (SshProfile candidate : profiles) {
                if (candidate.host.equalsIgnoreCase(host)) matches.add(candidate);
            }
            if (matches.isEmpty()) addProfile(host);
            else if (matches.size() == 1) open(matches.get(0));
            else {
                String[] labels = new String[matches.size()];
                for (int i = 0; i < matches.size(); i++) labels[i] = matches.get(i).label();
                new AlertDialog.Builder(activity).setTitle(R.string.ssh_choose_account)
                .setItems(labels,
                    (dialog, which) -> open(matches.get(which)))
                .setNegativeButton(android.R.string.cancel, null).show();
            }
        }
    }
    public void refresh() { render(); }
    private void render() {
        list.removeAllViews();
        java.util.List<SshProfile> profiles;
        try { profiles = SshProfile.load(activity); }
        catch (IllegalStateException e) {
            TextView error = new TextView(activity); error.setText(R.string.ssh_storage_error); list.addView(error);
            return;
        }
        if (profiles.isEmpty()) {
            TextView empty = new TextView(activity); empty.setText(R.string.ssh_empty); list.addView(empty);
        }
        for (SshProfile p : profiles) {
            LinearLayout row = new LinearLayout(activity);
            row.setOrientation(VERTICAL);
            Button connect = new Button(activity); connect.setText(p.host + "\n" + p.user + " · " + p.port);
            connect.setAllCaps(false);
            connect.setOnClickListener(v -> open(p)); row.addView(connect, new LinearLayout.LayoutParams(-1, -2));
            LinearLayout actions = new LinearLayout(activity);
            Button edit = new Button(activity); edit.setText("编辑");
            edit.setOnClickListener(v -> editProfile(p.host, p));
            actions.addView(edit, new LinearLayout.LayoutParams(0, -2, 1));
            Button remove = new Button(activity); remove.setText(R.string.ssh_forget);
            remove.setOnClickListener(v -> new AlertDialog.Builder(activity).setMessage(R.string.ssh_forget_detail)
                .setPositiveButton(R.string.ssh_forget, (d, w) -> {
                    try { p.remove(activity); render(); }
                    catch (IllegalStateException e) { Toast.makeText(activity, R.string.ssh_storage_error, Toast.LENGTH_LONG).show(); }
                })
                .setNegativeButton(android.R.string.cancel, null).show());
            actions.addView(remove, new LinearLayout.LayoutParams(0, -2, 1));
            row.addView(actions); list.addView(row);
        }
    }
    private EditText field(LinearLayout form, int hint, String value, int type) {
        int pad = Math.round(12 * getResources().getDisplayMetrics().density);
        TextView label = new TextView(activity);
        label.setText(hint);
        label.setTextSize(13);
        label.setPadding(0, pad, 0, pad / 2);
        form.addView(label);
        EditText edit = new EditText(activity); edit.setInputType(type); edit.setSingleLine(true);
        edit.setTextSize(16);
        edit.setBackgroundResource(R.drawable.bg_text_field);
        edit.setPadding(pad, pad, pad, pad);
        edit.setMinHeight(pad * 4);
        int fieldId = android.view.View.generateViewId();
        edit.setId(fieldId);
        label.setLabelFor(fieldId);
        edit.setText(value); form.addView(edit); return edit;
    }
    private void addProfile(String suggestedHost) {
        editProfile(suggestedHost, null);
    }
    private void editProfile(String suggestedHost, SshProfile previous) {
        LinearLayout form = new LinearLayout(activity); form.setOrientation(LinearLayout.VERTICAL); int inset = Math.round(24 * getResources().getDisplayMetrics().density); form.setPadding(inset, inset / 2, inset, inset / 2);
        EditText host = field(form, R.string.ssh_host, suggestedHost == null ? "" : suggestedHost, InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        EditText port = field(form, R.string.ssh_port, previous == null ? "22" : String.valueOf(previous.port), InputType.TYPE_CLASS_NUMBER);
        EditText user = field(form, R.string.ssh_user, previous == null ? "" : previous.user, InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        ScrollView scroll = new ScrollView(activity);
        scroll.addView(form);
        AlertDialog dialog = new AlertDialog.Builder(activity).setTitle(previous == null ? getString(R.string.ssh_add) : "编辑 SSH 连接").setView(scroll)
            .setPositiveButton(previous == null ? getString(R.string.ssh_connect) : "保存", null).setNegativeButton(android.R.string.cancel, null).create();
        dialog.setOnShowListener(d -> dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v -> {
            host.setError(null); port.setError(null); user.setError(null);
            try { new SshProfile(host.getText().toString(), "validation", 22); }
            catch (IllegalArgumentException e) { host.setError("请输入有效的主机名或 IP 地址"); host.requestFocus(); return; }
            int portNumber;
            try { portNumber = Integer.parseInt(port.getText().toString().trim());
                if (portNumber < 1 || portNumber > 65535) throw new NumberFormatException();
            } catch (NumberFormatException e) { port.setError("端口应为 1–65535"); port.requestFocus(); return; }
            try { new SshProfile("validation", user.getText().toString(), portNumber); }
            catch (IllegalArgumentException e) { user.setError("请输入远端 SSH 账户名"); user.requestFocus(); return; }
            try {
                SshProfile profile = new SshProfile(host.getText().toString(), user.getText().toString(), portNumber);
                profile.replace(activity, previous); dialog.dismiss(); render();
                if (previous == null) open(profile);
            } catch (IllegalArgumentException e) { host.setError(activity.getString(R.string.ssh_invalid_profile)); }
            catch (IllegalStateException e) { host.setError(activity.getString(R.string.ssh_storage_error)); }
        })); dialog.show();
    }
    private void open(SshProfile p) {
        activity.startActivity(new Intent(activity, TerminalActivity.class).putExtra("host", p.host).putExtra("port", p.port).putExtra("user", p.user));
    }
    private String getString(int id) { return activity.getString(id); }
}
