package com.limelight.piulink;

import com.limelight.utils.TouchDialog;

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
    private final LinearLayout targets, options, displayPage, virtualMode;
    private final Button more;
    private final CaptureOptions captureOptions;
    private final MonitorSelection.StreamMode streamMode;
    private final java.util.function.Consumer<DisplayInventoryClient.Mode> resized;
    private final java.util.function.BiPredicate<Integer,Integer> canDecodeSize;
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
            MonitorSelection.StreamMode streamMode, java.util.function.BiPredicate<Integer,Integer> canDecodeSize, java.util.function.Consumer<DisplayInventoryClient.Mode> resized, CaptureOptions captureOptions) {
        SessionDisplayControls panel = new SessionDisplayControls(activity, host, session, streamMode, canDecodeSize, resized, captureOptions);
        panel.dialog.show();
        panel.request("list", null, -1, activity.getString(R.string.monitor_loading));
        if (initial == null) panel.loadTargets();
        else panel.renderTargets(initial);
    }

    private SessionDisplayControls(Activity activity, String host, String session,
            MonitorSelection.StreamMode streamMode, java.util.function.BiPredicate<Integer,Integer> canDecodeSize, java.util.function.Consumer<DisplayInventoryClient.Mode> resized, CaptureOptions captureOptions) {
        this.activity = activity;
        this.host = host;
        this.session = session;
        this.captureOptions = captureOptions;
        this.resized = resized;
        this.streamMode = streamMode;
        this.canDecodeSize = canDecodeSize;
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
        displayPage = new LinearLayout(activity);
        displayPage.setOrientation(LinearLayout.VERTICAL);
        targets = new LinearLayout(activity);
        targets.setOrientation(LinearLayout.VERTICAL);
        displayPage.addView(targets);
        content = new LinearLayout(activity);
        content.setOrientation(LinearLayout.VERTICAL);
        panel.addView(content);
        more = new Button(activity);
        styleAction(more, R.drawable.ic_computer);
        more.setText(R.string.screen_more_options);
        more.setVisibility(View.GONE);
        virtualMode = new LinearLayout(activity);
        virtualMode.setOrientation(LinearLayout.VERTICAL);
        displayPage.addView(virtualMode);
        displayPage.addView(more);
        options = new LinearLayout(activity);
        options.setOrientation(LinearLayout.VERTICAL);
        options.setVisibility(View.GONE);
        displayPage.addView(options);
        panel.addView(displayPage);
        displayPage.setVisibility(View.GONE);
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
        LinearLayout header = new LinearLayout(activity);
        header.setOrientation(LinearLayout.VERTICAL);
        header.addView(title);
        LinearLayout tabs = new LinearLayout(activity);
        tabs.setPadding(dp(16), 0, dp(16), dp(8));
        header.addView(tabs);
        Button picture = choice(activity.getString(R.string.session_picture), true);
        Button displays = choice(activity.getString(R.string.session_displays), false);
        tabs.addView(picture, equalCell());
        tabs.addView(displays, equalCell());
        View.OnClickListener switchPage = tab -> {
            boolean showPicture = tab == picture;
            setChosen(picture, showPicture);
            setChosen(displays, !showPicture);
            content.setVisibility(showPicture ? View.VISIBLE : View.GONE);
            displayPage.setVisibility(showPicture ? View.GONE : View.VISIBLE);
            scroll.scrollTo(0, 0);
        };
        picture.setOnClickListener(switchPage);
        displays.setOnClickListener(switchPage);
        dialog = new TouchDialog(activity)
                .setTitle(R.string.monitor_select_title).setCustomTitle(header).setView(scroll)
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

    private void disablePictureActions(ViewGroup group) {
        for (int i = 0; i < group.getChildCount(); i++) {
            View child = group.getChildAt(i);
            if (child instanceof Button) child.setEnabled(false);
            else if (child instanceof ViewGroup) disablePictureActions((ViewGroup) child);
        }
    }

    private void request(String action, String key, int count, String waiting) {
        request(action, key, count, waiting, null);
    }

    private void request(String action, String key, int count, String waiting, DisplayInventoryClient.Mode mode) {
        if (requesting || !visible()) return;
        scroll.removeCallbacks(poll);
        boolean automatic = waiting == null;
        int number = ++requestNumber;
        if (!automatic) {
            requesting = true;
            status.setText(waiting);
            status.setVisibility(View.VISIBLE);
            disablePictureActions(content);
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
                JSONObject result = DisplayInventoryClient.sessionDisplays(activity, host, session, action, key, count, automatic, mode);
                if ("stream-mode".equals(action)) {
                    JSONObject video = result.optJSONObject("encodedVideo");
                    if (video == null || video.optInt("fps") != mode.fps || video.optInt("width") != mode.width ||
                            video.optInt("height") != mode.height) throw new DisplayInventoryClient.SessionError("display_mode_unavailable");
                    activity.runOnUiThread(() -> {
                        if (!activity.isFinishing() && !activity.isDestroyed()) streamMode.applied(mode.fps);
                    });
                } else if (mode != null) {
                    DisplayInventoryClient.Mode actual = DisplayInventoryClient.confirmedVirtualMode(result, key, mode);
                    activity.runOnUiThread(() -> {
                        if (!activity.isFinishing() && !activity.isDestroyed()) resized.accept(actual);
                    });
                }
                activity.runOnUiThread(() -> { if (visible() && number == requestNumber) render(result); });
            } catch (Exception error) {
                String reason = error instanceof DisplayInventoryClient.SessionError
                        ? ((DisplayInventoryClient.SessionError) error).reason : "";
                activity.runOnUiThread(() -> {
                    if (visible() && number == requestNumber) {
                        if (automatic && error instanceof java.io.IOException &&
                                !(error instanceof DisplayInventoryClient.SessionError) && content.getChildCount() > 0) {
                            // A failed poll cannot invalidate the last confirmed picture mode.
                            // Keep it readable and renew it before accepting another change.
                            renderedState = null;
                            disablePictureActions(content);
                            status.setText(R.string.screen_refresh_retry);
                            status.setVisibility(View.VISIBLE);
                            schedulePoll();
                        } else showError(reason, automatic);
                    }
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
        if (captured) {
            for (int i = 0; i < outputs.length(); i++) {
                JSONObject output = outputs.optJSONObject(i);
                if (output != null && output.optBoolean("capture") && output.optBoolean("active")) {
                    addStreamModes(output.optString("key"), state.optJSONObject("encodedVideo"));
                    break;
                }
            }
        }
        if (captured && content.getChildCount() == 0) label(activity.getString(R.string.screen_video_resize_update));
        virtualMode.removeAllViews();
        if (captured) addResolutionPicker(state.optJSONObject("virtualDesktop"), state.optJSONObject("encodedVideo"));
        if (scrollY > 0) scroll.post(() -> scroll.scrollTo(0, scrollY));
    }

    private String outputType(JSONObject row) {
        return activity.getString(row.optBoolean("virtual") ? R.string.monitor_virtual : R.string.monitor_physical);
    }

    private void addStreamModes(String key, JSONObject video) {
        if (video == null || video.optInt("fps") <= 0) return;
        int w = video.optInt("width"), h = video.optInt("height"), rate = video.optInt("fps");
        if (w <= 0 || h <= 0 || key.length() != 64) return;
        java.util.List<DisplayInventoryClient.Mode> sizes = new java.util.ArrayList<>();
        java.util.SortedSet<Integer> edges = new java.util.TreeSet<>(java.util.Arrays.asList(1280, 1920, 2560, 3840, Math.max(w,h)));
        for (int edge : edges) {
            float scale = edge / (float)Math.max(w,h);
            int width = 2 * Math.round(w * scale / 2), height = 2 * Math.round(h * scale / 2);
            if (width == w && height == h || streamMode.supports(width, height, rate))
                sizes.add(new DisplayInventoryClient.Mode(width, height, rate));
        }
        streamPicker(key, video, sizes, false);
        java.util.List<DisplayInventoryClient.Mode> rates = new java.util.ArrayList<>();
        for (int fps : new java.util.TreeSet<>(java.util.Arrays.asList(24, 30, 45, 60, 90, 120, 144, rate)))
            if (fps == rate || streamMode.supports(w, h, fps)) rates.add(new DisplayInventoryClient.Mode(w, h, fps));
        streamPicker(key, video, rates, true);
    }

    private void streamPicker(String key, JSONObject video, java.util.List<DisplayInventoryClient.Mode> modes, boolean fps) {
        String title = activity.getString(fps ? R.string.session_fps : R.string.session_resolution);
        TextView heading = label(title);
        heading.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        heading.setTextColor(activity.getColor(R.color.home_ink));
        if (android.os.Build.VERSION.SDK_INT >= 28) heading.setAccessibilityHeading(true);
        int columns = fps ? 3 : 2;
        LinearLayout row = null;
        for (int i = 0; i < modes.size(); i++) {
            if (i % columns == 0) { row = new LinearLayout(activity); content.addView(row); }
            DisplayInventoryClient.Mode mode = modes.get(i);
            String value = fps ? mode.fps + " FPS" : mode.width + " × " + mode.height;
            boolean selected = video.optInt("width") == mode.width && video.optInt("height") == mode.height &&
                    video.optInt("fps") == mode.fps;
            Button button = choice(value, selected);
            button.setContentDescription(title + " · " + value);
            button.setOnClickListener(v -> {
                if (!selected && !requesting) request("stream-mode", key, -1,
                        activity.getString(R.string.screen_resizing), mode);
            });
            row.addView(button, equalCell());
        }
        for (int i = modes.size(); i % columns != 0; i++) row.addView(new View(activity), equalCell());
    }

    private LinearLayout.LayoutParams equalCell() {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, -2, 1);
        params.setMargins(dp(2), dp(2), dp(2), dp(2));
        return params;
    }

    private Button choice(String text, boolean selected) {
        Button button = new Button(activity);
        button.setAllCaps(false);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setMinHeight(dp(48));
        button.setTextSize(14);
        button.setText(text);
        button.setGravity(Gravity.CENTER);
        button.setStateListAnimator(null);
        button.setPadding(dp(4), dp(4), dp(4), dp(4));
        setChosen(button, selected);
        return button;
    }

    private void setChosen(Button button, boolean selected) {
        button.setSelected(selected);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setTextColor(activity.getColor(selected ? R.color.piulink_on_accent : R.color.home_ink));
        button.setBackgroundResource(selected ? R.drawable.bg_home_desktop : R.drawable.bg_home_add);
    }

    private void addResolutionPicker(JSONObject desktop, JSONObject video) {
        if (desktop == null) return;
        if (video == null) return;
        JSONObject current = desktop.optJSONObject("current");
        JSONArray modes = desktop.optJSONArray("modes");
        if (current == null || modes == null || modes.length() == 0) return;
        String key = desktop.optString("key");
        java.util.List<DisplayInventoryClient.Mode> choices = new java.util.ArrayList<>();
        java.util.List<String> labels = new java.util.ArrayList<>();
        int selected = -1;
        for (int i = 0; i < modes.length(); i++) {
            JSONObject item = modes.optJSONObject(i);
            if (item == null || item.optInt("width") <= 0 || item.optInt("height") <= 0 ||
                    item.optInt("fps") != current.optInt("fps")) continue;
            int w = item.optInt("width"), h = item.optInt("height");
            if (!canDecodeSize.test(w, h)) continue;
            if (w == current.optInt("width") && h == current.optInt("height") &&
                    w == video.optInt("width") && h == video.optInt("height")) selected = choices.size();
            choices.add(new DisplayInventoryClient.Mode(w, h, item.optInt("fps")));
            labels.add(w + " × " + h);
        }
        if (choices.isEmpty()) { label(virtualMode, activity.getString(R.string.screen_video_resize_decoder)); return; }
        Button resize = new Button(activity);
        styleAction(resize, R.drawable.ic_computer);
        resize.setText(activity.getString(R.string.screen_virtual_resolution, current.optInt("width"), current.optInt("height")));
        final int checked = selected;
        resize.setOnClickListener(v -> {
            if (requesting) return;
            new TouchDialog(activity).setTitle(R.string.screen_virtual_resolution_title)
                    .setSingleChoiceItems(labels.toArray(new String[0]), checked, (picker, which) -> {
                        picker.dismiss();
                        if (which != checked) request("resize-stream", key, -1, activity.getString(R.string.screen_resizing), choices.get(which));
                    }).setNegativeButton(android.R.string.cancel, null).show();
        });
        virtualMode.addView(resize);
    }

    private void showError(String reason, boolean automatic) {
        requesting = false;
        renderedState = null;
        content.removeAllViews();
        virtualMode.removeAllViews();
        liveOutputs = null;
        if (inventory != null) renderTargets(inventory);
        int message;
        switch (reason) {
            case "display_mode_unavailable": message = R.string.screen_resize_unavailable; break;
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
        return label(content, text);
    }

    private TextView label(LinearLayout parent, String text) {
        TextView label = new TextView(activity);
        label.setText(text);
        label.setTextColor(activity.getColor(R.color.home_muted));
        label.setTextSize(13);
        label.setPadding(dp(4), dp(6), dp(4), dp(10));
        parent.addView(label);
        return label;
    }
}
