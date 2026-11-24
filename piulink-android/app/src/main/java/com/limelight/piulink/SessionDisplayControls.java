package com.limelight.piulink;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.view.Gravity;
import android.view.Window;
import android.view.WindowManager;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Switch;
import android.widget.TextView;

import com.limelight.R;
import org.json.JSONArray;
import org.json.JSONObject;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Owns one stream's display panel. Commands update the UI only after Windows acknowledges them. */
public final class SessionDisplayControls {
    private static final long REFRESH_INTERVAL_MS = 5000;
    private final Activity activity;
    private final String host, session;
    private final LinearLayout content;
    private final ScrollView scroll;
    private final TextView status;
    private final LinearLayout targets, options;
    private final Button more;
    private final CaptureOptions captureOptions;
    final AlertDialog dialog;
    private final ExecutorService requests = Executors.newSingleThreadExecutor(r ->
            new Thread(r, "PiuLink session displays"));
    private final Runnable poll = this::pollWhileVisible;
    private boolean requesting;
    private int requestNumber;
    private String renderedState;
    private int inventoryGeneration;
    private DisplayInventoryClient.Inventory inventory;
    private JSONArray liveOutputs;
    private final java.util.Set<String> shownKeys = new java.util.HashSet<>();

    interface CaptureOptions {
        void render(SessionDisplayControls panel, DisplayInventoryClient.Inventory inventory);
    }

    static void show(Activity activity, String host, String session, DisplayInventoryClient.Inventory initial,
            CaptureOptions captureOptions) {
        SessionDisplayControls panel = new SessionDisplayControls(activity, host, session, captureOptions);
        panel.dialog.show();
        panel.request("list", null, -1, activity.getString(R.string.monitor_loading));
        if (initial == null) panel.loadTargets();
        else panel.renderTargets(initial);
    }

    private SessionDisplayControls(Activity activity, String host, String session, CaptureOptions captureOptions) {
        this.activity = activity;
        this.host = host;
        this.session = session;
        this.captureOptions = captureOptions;
        LinearLayout panel = new LinearLayout(activity);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(20), dp(6), dp(20), dp(6));
        status = new TextView(activity);
        status.setTextColor(activity.getColor(R.color.home_muted));
        status.setTextSize(14);
        status.setPadding(0, dp(6), 0, dp(8));
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        status.setVisibility(View.GONE);
        panel.addView(status);
        targets = new LinearLayout(activity);
        targets.setOrientation(LinearLayout.VERTICAL);
        panel.addView(targets);
        content = new LinearLayout(activity);
        content.setOrientation(LinearLayout.VERTICAL);
        panel.addView(content);
        more = new Button(activity);
        styleAction(more, R.drawable.ic_computer);
        more.setText(R.string.screen_more_options);
        more.setVisibility(View.GONE);
        panel.addView(more);
        options = new LinearLayout(activity);
        options.setOrientation(LinearLayout.VERTICAL);
        options.setVisibility(View.GONE);
        panel.addView(options);
        more.setOnClickListener(v -> options.setVisibility(options.getVisibility() == View.VISIBLE ? View.GONE : View.VISIBLE));
        scroll = new ScrollView(activity);
        scroll.addView(panel);
        TextView title = new TextView(activity);
        title.setText(R.string.monitor_select_title);
        title.setTextSize(22);
        title.setTextColor(activity.getColor(R.color.home_ink));
        title.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        title.setPadding(dp(20), dp(18), dp(20), dp(8));
        if (android.os.Build.VERSION.SDK_INT >= 28) title.setAccessibilityHeading(true);
        dialog = new AlertDialog.Builder(activity)
                .setTitle(R.string.monitor_select_title).setCustomTitle(title).setView(scroll)
                .setNeutralButton(R.string.monitor_refresh, null).setNegativeButton(R.string.screen_close_panel, null).create();
        dialog.setOnShowListener(d -> {
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setOnClickListener(v -> refresh());
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setAllCaps(false);
            dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setTextColor(activity.getColorStateList(R.color.display_action_text));
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).setAllCaps(false);
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).setTextColor(activity.getColor(R.color.home_ink));
            Window window = dialog.getWindow();
            if (window != null) {
                window.setBackgroundDrawableResource(R.drawable.bg_display_window);
                int available = activity.getWindow().getDecorView().getWidth();
                if (available <= 0) available = activity.getResources().getDisplayMetrics().widthPixels;
                boolean side = activity.getResources().getConfiguration().orientation ==
                        android.content.res.Configuration.ORIENTATION_LANDSCAPE && available >= dp(560);
                WindowManager.LayoutParams bounds = window.getAttributes();
                bounds.width = side ? Math.min(dp(420), Math.max(dp(300), Math.round(available * 0.38f)))
                        : Math.min(dp(440), Math.max(1, available - dp(24)));
                bounds.gravity = side ? Gravity.END | Gravity.CENTER_VERTICAL : Gravity.CENTER;
                bounds.x = side ? dp(24) : 0;
                bounds.dimAmount = side ? 0.18f : 0.35f;
                window.setAttributes(bounds);
            }
        });
        dialog.setOnDismissListener(d -> {
            scroll.removeCallbacks(poll);
            requests.shutdown();
        });
    }

    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }

    void styleAction(Button button, int icon) {
        int ink = activity.getColor(R.color.home_ink);
        int muted = activity.getColor(R.color.home_muted);
        ColorStateList colors = new ColorStateList(new int[][] {{-android.R.attr.state_enabled}, {}}, new int[] {muted, ink});
        button.setAllCaps(false);
        button.setStateListAnimator(null);
        button.setMinHeight(dp(48));
        button.setTextSize(15);
        button.setTextColor(colors);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        button.setPaddingRelative(dp(14), dp(6), dp(14), dp(6));
        button.setBackgroundResource(R.drawable.bg_home_add);
        Drawable glyph = activity.getDrawable(icon).mutate();
        glyph.setTintList(colors);
        button.setCompoundDrawablesRelativeWithIntrinsicBounds(glyph, null, null, null);
        button.setCompoundDrawablePadding(dp(10));
    }

    private void refresh() {
        request("list", null, -1, activity.getString(R.string.monitor_loading));
        loadTargets();
    }

    private void loadTargets() {
        if (host == null || !visible()) return;
        int generation = ++inventoryGeneration;
        requests.execute(() -> {
            DisplayInventoryClient.Inventory inventory = null;
            try { inventory = DisplayInventoryClient.fetch(activity, host); }
            catch (Exception ignored) { }
            final DisplayInventoryClient.Inventory result = inventory;
            activity.runOnUiThread(() -> {
                if (visible() && generation == inventoryGeneration) renderTargets(result);
            });
        });
    }

    private void renderTargets(DisplayInventoryClient.Inventory inventory) {
        this.inventory = inventory;
        shownKeys.clear();
        targets.removeAllViews();
        options.removeAllViews();
        more.setVisibility(View.GONE);
        if (inventory != null && !inventory.displays.isEmpty()) {
            captureOptions.render(this, inventory);
            if (liveOutputs != null) {
                for (int i = 0; i < liveOutputs.length(); i++) {
                    JSONObject output = liveOutputs.optJSONObject(i);
                    if (output != null && !shownKeys.contains(output.optString("key")))
                        addScreenRow(output.optString("name"), outputType(output), output, output.optBoolean("capture"), null);
                }
            }
        } else {
            TextView failure = new TextView(activity);
            failure.setText(R.string.screen_targets_retry);
            targets.addView(failure);
        }
    }

    void addTarget(DisplayInventoryClient.Display display, boolean current, Runnable select) {
        JSONObject match = null;
        // Legacy hosts expose separate control and capture IDs. Only join uniquely identified outputs.
        if (liveOutputs != null && inventory.displays.stream().filter(d -> d.name.equals(display.name)
                && d.virtual == display.virtual).count() == 1) {
            for (int i = 0; i < liveOutputs.length(); i++) {
                JSONObject candidate = liveOutputs.optJSONObject(i);
                if (candidate == null || !display.name.equals(candidate.optString("name")) ||
                        candidate.optBoolean("virtual") != display.virtual) continue;
                if (match != null) { match = null; break; }
                match = candidate;
            }
        }
        if (match != null) shownKeys.add(match.optString("key"));
        boolean captured = match != null ? match.optBoolean("capture") : current;
        String detail = activity.getString(display.virtual ? R.string.monitor_virtual : R.string.monitor_physical);
        if (display.isActive()) detail += "  ·  " + display.width + " × " + display.height;
        addScreenRow(display.name, detail, match, captured, display.isActive() && !captured ? select : null);
    }

    private void addScreenRow(String name, String detail, JSONObject output, boolean current, Runnable select) {
        LinearLayout row = new LinearLayout(activity);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPaddingRelative(dp(12), dp(3), dp(12), dp(3));
        row.setBackgroundResource(R.drawable.bg_display_capture);
        LinearLayout.LayoutParams space = new LinearLayout.LayoutParams(-1, -2);
        space.bottomMargin = dp(6);
        targets.addView(row, space);
        LinearLayout text = new LinearLayout(activity);
        text.setOrientation(LinearLayout.VERTICAL);
        text.setGravity(Gravity.CENTER_VERTICAL);
        text.setMinimumHeight(dp(54));
        TextView nameView = new TextView(activity);
        nameView.setText(name);
        nameView.setTextSize(16);
        nameView.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        nameView.setTextColor(activity.getColor(R.color.home_ink));
        TextView caption = new TextView(activity);
        caption.setText(current ? activity.getString(R.string.screen_current_caption, detail) : detail);
        caption.setTextSize(13);
        caption.setTextColor(activity.getColor(current ? R.color.piulink_success : R.color.home_muted));
        text.addView(nameView);
        text.addView(caption);
        row.addView(text, new LinearLayout.LayoutParams(0, -2, 1));
        if (select != null) {
            Button choose = new Button(activity);
            choose.setText(R.string.screen_switch);
            choose.setContentDescription(activity.getString(R.string.screen_switch_to, name));
            styleAction(choose, R.drawable.ic_computer);
            choose.setCompoundDrawables(null, null, null, null);
            choose.setGravity(Gravity.CENTER);
            choose.setMinWidth(dp(58));
            View.OnClickListener switchHere = v -> { if (!requesting) { dialog.dismiss(); select.run(); } };
            text.setOnClickListener(switchHere);
            choose.setOnClickListener(switchHere);
            row.addView(choose);
        }
        if (output == null || output.optBoolean("capture")) return;
        String key = output.optString("key");
        boolean active = output.optBoolean("active");
        Switch toggle = new Switch(activity);
        toggle.setContentDescription(activity.getString(R.string.screen_output_name, name,
                activity.getString(active ? R.string.screen_on : R.string.screen_off)));
        toggle.setMinHeight(dp(48));
        toggle.setPaddingRelative(dp(12), 0, 0, 0);
        toggle.setThumbTintList(activity.getColorStateList(R.color.display_switch_thumb));
        toggle.setTrackTintList(activity.getColorStateList(R.color.display_switch_track));
        toggle.setChecked(active);
        toggle.setEnabled(!key.isEmpty() && canManageOutputs());
        toggle.setOnCheckedChangeListener((button, wanted) -> {
            if (wanted == active) return;
            button.setChecked(active);
            if (requesting) return;
            request(wanted ? "enable" : "disable", key, -1,
                    activity.getString(wanted ? R.string.screen_enabling : R.string.screen_disabling, name));
        });
        row.addView(toggle);
    }

    private boolean canManageOutputs() {
        if (liveOutputs != null) for (int i = 0; i < liveOutputs.length(); i++) {
            JSONObject output = liveOutputs.optJSONObject(i);
            if (output != null && output.optBoolean("capture") && output.optBoolean("active")) return true;
        }
        return false;
    }

    void setOptions(View settings) {
        options.addView(settings);
        more.setVisibility(View.VISIBLE);
    }

    private void schedulePoll() {
        scroll.removeCallbacks(poll);
        if (visible()) scroll.postDelayed(poll, REFRESH_INTERVAL_MS);
    }

    private void pollWhileVisible() {
        if (!visible() || requesting) return;
        if (scroll.hasWindowFocus() && scroll.getWindowVisibility() == View.VISIBLE) {
            request("list", null, -1, null);
        } else {
            schedulePoll();
        }
    }

    private void disableOutputActions() {
        for (int i = 0; i < content.getChildCount(); i++) {
            View child = content.getChildAt(i);
            if (child.isClickable()) child.setEnabled(false);
        }
    }

    private void request(String action, String key, int count, String waiting) {
        if (requesting || !visible()) return;
        scroll.removeCallbacks(poll);
        boolean automatic = waiting == null;
        int number = ++requestNumber;
        if (!automatic) {
            requesting = true;
            status.setText(waiting);
            status.setVisibility(View.VISIBLE);
            disableOutputActions();
            setNavigationEnabled(false);
        }
        if (DisplayInventoryClient.lastInventoryWasLegacy(host) || (inventory != null && !inventory.supportsSessionDisplays)) {
            showError("unsupported", automatic);
            return;
        }
        if (session == null || host == null) {
            showError("session_required", automatic);
            return;
        }
        // An explicit tap queues behind an ongoing read. That older reply must not undo the pending action.
        requests.execute(() -> {
            try {
                JSONObject result = DisplayInventoryClient.sessionDisplays(activity, host, session, action, key, count, automatic);
                activity.runOnUiThread(() -> { if (visible() && number == requestNumber) render(result); });
            } catch (Exception error) {
                String reason = error instanceof DisplayInventoryClient.SessionError
                        ? ((DisplayInventoryClient.SessionError) error).reason : "";
                activity.runOnUiThread(() -> {
                    if (visible() && number == requestNumber) showError(reason, automatic);
                });
            }
        });
    }

    private boolean visible() { return dialog.isShowing() && !activity.isFinishing() && !activity.isDestroyed(); }

    void render(JSONObject state) {
        JSONArray outputs = state.optJSONArray("outputs");
        if (outputs == null) { showError("", false); return; }
        schedulePoll();
        String current = state.toString();
        if (!requesting && current.equals(renderedState)) return;
        boolean topologyChanged = renderedState != null && !renderedState.equals(current);
        renderedState = current;
        if (topologyChanged) {
            DisplayInventoryClient.invalidate(host);
            loadTargets();
        }
        int scrollY = scroll.getScrollY();
        requesting = false;
        setNavigationEnabled(true);
        status.setVisibility(View.GONE);
        content.removeAllViews();
        liveOutputs = outputs;
        boolean captured = canManageOutputs();
        if (inventory != null) renderTargets(inventory);
        if (!captured) label(activity.getString(R.string.screen_capture_unknown));
        int count = state.optInt("virtualCount", -1);
        Button create = new Button(activity);
        styleAction(create, R.drawable.ic_add);
        create.setText(R.string.screen_create);
        create.setEnabled(captured && count >= 0 && count < 16 && "available".equals(state.optString("createStatus")));
        create.setOnClickListener(v -> new AlertDialog.Builder(activity)
                .setMessage(R.string.screen_create_confirm).setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.screen_create, (d, w) ->
                        request("create", null, count, activity.getString(R.string.screen_creating))).show());
        content.addView(create);
        if (!create.isEnabled()) create.setAlpha(0.75f);
        if (!create.isEnabled()) label(activity.getString(createHint(state.optString("createStatus"))));
        if (scrollY > 0) scroll.post(() -> scroll.scrollTo(0, scrollY));
    }

    private String outputType(JSONObject row) {
        return activity.getString(row.optBoolean("virtual") ? R.string.monitor_virtual : R.string.monitor_physical);
    }

    private static int createHint(String reason) {
        switch (reason) {
            case "driver_required": return R.string.screen_driver_required;
            case "enable_driver": return R.string.screen_driver_disabled;
            case "physical_capture_required": return R.string.screen_physical_required;
            case "limit_reached": return R.string.screen_limit;
            default: return R.string.screen_driver_unavailable;
        }
    }

    private void showError(String reason, boolean automatic) {
        requesting = false;
        renderedState = null;
        content.removeAllViews();
        liveOutputs = null;
        if (inventory != null) renderTargets(inventory);
        int message;
        switch (reason) {
            case "unsupported": message = R.string.screen_live_unavailable; break;
            case "session_required": message = R.string.screen_session_required; break;
            case "output_protected": message = R.string.screen_capture_unknown; break;
            case "creation_unconfirmed": message = R.string.screen_create_unconfirmed; break;
            case "outputs_changed": message = R.string.screen_changed; break;
            case "output_restore_pending": message = R.string.screen_restore_pending; break;
            default: message = automatic ? R.string.screen_refresh_retry : R.string.screen_failed;
        }
        if (!activity.getString(message).contentEquals(status.getText())) status.setText(message);
        status.setVisibility(View.VISIBLE);
        setNavigationEnabled(true);
        if (!automatic) scroll.smoothScrollTo(0, 0);
        if (automatic && !"unsupported".equals(reason) && !"session_required".equals(reason)) schedulePoll();
    }

    private void setNavigationEnabled(boolean enabled) {
        for (int i = 0; i < targets.getChildCount(); i++) targets.getChildAt(i).setEnabled(enabled);
        more.setEnabled(enabled);
        if (!enabled) options.setVisibility(View.GONE);
        dialog.getButton(AlertDialog.BUTTON_NEUTRAL).setEnabled(enabled);
    }

    private TextView label(String text) {
        TextView label = new TextView(activity);
        label.setText(text);
        label.setTextColor(activity.getColor(R.color.home_muted));
        label.setTextSize(13);
        label.setPadding(dp(4), dp(6), dp(4), dp(10));
        content.addView(label);
        return label;
    }
}
