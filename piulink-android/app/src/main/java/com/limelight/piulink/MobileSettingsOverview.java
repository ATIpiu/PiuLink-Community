package com.limelight.piulink;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Typeface;
import android.os.Build;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.utils.TailscaleLauncher;

import java.text.NumberFormat;
import java.util.function.IntConsumer;

/** The phone and tablet entry points use the same preferences as the full settings pages. */
final class MobileSettingsOverview extends LinearLayout {
    private final Activity activity;
    private final LinearLayout features, categories;
    private final QualityModeBar modes;
    private final View quality;
    private final TextView bitrate, soundValue, touchValue, gamepadValue;
    private final String[] modeValues;
    private int columns;

    MobileSettingsOverview(Activity activity, IntConsumer open, View[] sections) {
        super(activity);
        this.activity = activity;
        setOrientation(VERTICAL);
        setGravity(Gravity.TOP);
        setPadding(0, 0, 0, dp(20));
        features = column();
        categories = column();
        addView(features);
        addView(categories);

        View card = activity.getLayoutInflater().inflate(R.layout.settings_quality_card, features, false);
        features.addView(card);
        quality = sections[0] = card.findViewById(R.id.settingsQualityEntry);
        bitrate = card.findViewById(R.id.settingsBitrateValue);
        modes = card.findViewById(R.id.settingsModes);
        quality.setOnClickListener(v -> open.accept(0));
        modeValues = getResources().getStringArray(R.array.auto_bitrate_mode_values);
        String[] names = getResources().getStringArray(R.array.auto_bitrate_mode_names);
        for (int i = 0; i < modeValues.length; i++) {
            Button choice = new Button(activity);
            String value = modeValues[i];
            choice.setText(names[i]);
            choice.setTextSize(14);
            choice.setAllCaps(false);
            choice.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
            choice.setMinWidth(0);
            choice.setMinimumWidth(0);
            choice.setMinimumHeight(dp(48));
            choice.setPadding(dp(3), 0, dp(3), 0);
            choice.setStateListAnimator(null);
            choice.setTextColor(activity.getColorStateList(R.color.settings_mode_text));
            choice.setBackgroundResource(R.drawable.bg_settings_mode);
            choice.setOnClickListener(v -> {
                PreferenceConfiguration.setAutomaticBitrateMode(activity, value);
                refresh(true);
            });
            modes.addView(choice, new LayoutParams(0, -2, 1));
        }

        LinearLayout quick = new LinearLayout(activity);
        quick.setGravity(Gravity.CENTER_VERTICAL);
        quick.setBaselineAligned(false);
        LayoutParams quickParams = new LayoutParams(-1, -2);
        quickParams.topMargin = dp(13);
        features.addView(quick, quickParams);
        soundValue = quickEntry(quick, R.drawable.ic_settings_sound, R.string.settings_sound, 1, open, sections);
        View divider = new View(activity);
        divider.setBackgroundColor(activity.getColor(R.color.piulink_divider));
        quick.addView(divider, new LayoutParams(dp(1), dp(43)));
        touchValue = quickEntry(quick, R.drawable.ic_settings_touch, R.string.settings_touch, 3, open, sections);

        group(R.string.settings_devices_group);
        sections[2] = row(R.string.settings_hardware_pad, () -> open.accept(2), null);
        gamepadValue = label(13, R.color.piulink_text_secondary);
        sections[4] = row(R.string.settings_overlay_pad, () -> open.accept(4), gamepadValue);
        sections[5] = row(R.string.settings_host_label, () -> open.accept(5), null);
        group(R.string.settings_client_group);
        sections[6] = row(R.string.settings_appearance, () -> open.accept(6), null);
        sections[7] = row(R.string.settings_advanced_label, () -> open.accept(7), null);
        row(R.string.settings_connection_guide, () -> activity.startActivity(new Intent(activity, SetupActivity.class)), null);
        row(R.string.open_tailscale, () -> TailscaleLauncher.openAppOrPlayStore(activity), null);
    }

    void refresh() {
        refresh(false);
    }

    private void refresh(boolean animate) {
        PreferenceConfiguration prefs = PreferenceConfiguration.readPreferences(activity);
        NumberFormat number = NumberFormat.getNumberInstance();
        number.setMaximumFractionDigits(2);
        String current = prefs.autoBitrate ? activity.getString(R.string.settings_quality_auto)
                : activity.getString(R.string.settings_quality_manual, number.format(prefs.bitrate / 1000.0));
        bitrate.setText(current);
        String mode = prefs.autoBitrate ? current : activity.getString(R.string.settings_quality_fixed) + " · " + current;
        quality.setContentDescription(activity.getString(R.string.settings_quality) + " · " + mode);
        modes.setVisibility(prefs.autoBitrate ? VISIBLE : GONE);
        int selectedMode = java.util.Arrays.asList(modeValues).indexOf(prefs.autoBitrateMode);
        modes.select(selectedMode >= 0 ? selectedMode : java.util.Arrays.asList(modeValues).indexOf("balanced"), animate);
        soundValue.setText(PreferenceConfiguration.getAudioConfigurationLabel(activity));
        touchValue.setText(prefs.touchscreenTrackpad ? R.string.settings_touch_trackpad : R.string.settings_touch_direct);
        gamepadValue.setText(prefs.onscreenController ? R.string.settings_on_state : R.string.settings_off_state);
    }

    private TextView quickEntry(LinearLayout parent, int icon, int title, int index, IntConsumer open, View[] sections) {
        LinearLayout entry = column();
        entry.setMinimumHeight(dp(100));
        entry.setGravity(Gravity.CENTER_VERTICAL);
        entry.setPaddingRelative(dp(12), dp(8), dp(8), dp(8));
        action(entry, () -> open.accept(index));
        ImageView glyph = new ImageView(activity);
        glyph.setImageResource(icon);
        glyph.setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
        entry.addView(glyph, new LayoutParams(dp(24), dp(24)));
        TextView name = label(17, R.color.piulink_text_primary);
        name.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        name.setText(title);
        name.setPadding(0, dp(5), 0, 0);
        entry.addView(name);
        TextView value = label(13, R.color.piulink_text_secondary);
        entry.addView(value);
        parent.addView(entry, new LayoutParams(0, -2, 1));
        sections[index] = entry;
        return value;
    }

    private void group(int title) {
        TextView heading = label(13, R.color.piulink_text_secondary);
        heading.setText(title);
        heading.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        if (Build.VERSION.SDK_INT >= 28) heading.setAccessibilityHeading(true);
        LayoutParams params = new LayoutParams(-1, -2);
        params.topMargin = categories.getChildCount() == 0 ? 0 : dp(20);
        heading.setPaddingRelative(dp(2), dp(6), 0, dp(8));
        categories.addView(heading, params);
        View rule = new View(activity);
        rule.setBackgroundColor(activity.getColor(R.color.piulink_divider));
        categories.addView(rule, new LayoutParams(-1, dp(1)));
    }

    private View row(int title, Runnable onClick, TextView value) {
        LinearLayout line = new LinearLayout(activity);
        line.setGravity(Gravity.CENTER_VERTICAL);
        line.setMinimumHeight(dp(50));
        line.setPaddingRelative(dp(2), dp(6), dp(3), dp(6));
        action(line, onClick);
        TextView name = label(16, R.color.piulink_text_primary);
        name.setText(title);
        line.addView(name, new LayoutParams(0, -2, 1));
        if (value != null) {
            value.setPaddingRelative(dp(8), 0, dp(9), 0);
            line.addView(value);
        }
        ImageView arrow = new ImageView(activity);
        arrow.setImportantForAccessibility(IMPORTANT_FOR_ACCESSIBILITY_NO);
        arrow.setImageResource(R.drawable.ic_section_next);
        line.addView(arrow, new LayoutParams(dp(14), dp(14)));
        categories.addView(line, new LayoutParams(-1, -2));
        return line;
    }

    private void action(View view, Runnable run) {
        TypedValue ripple = new TypedValue();
        activity.getTheme().resolveAttribute(android.R.attr.selectableItemBackground, ripple, true);
        view.setBackgroundResource(ripple.resourceId);
        view.setFocusable(true);
        view.setOnClickListener(v -> run.run());
    }

    private TextView label(int size, int color) {
        TextView text = new TextView(activity);
        text.setTextSize(size);
        text.setTextColor(activity.getColor(color));
        return text;
    }

    private LinearLayout column() {
        LinearLayout view = new LinearLayout(activity);
        view.setOrientation(VERTICAL);
        return view;
    }

    @Override protected void onMeasure(int width, int height) {
        boolean wide = MeasureSpec.getSize(width) >= dp(660);
        int count = wide ? 2 : 1;
        if (count != columns) {
            columns = count;
            setOrientation(wide ? HORIZONTAL : VERTICAL);
            features.setLayoutParams(new LayoutParams(wide ? 0 : -1, -2, wide ? 1.05f : 0));
            LayoutParams right = new LayoutParams(wide ? 0 : -1, -2, wide ? 1 : 0);
            right.setMarginStart(wide ? dp(36) : 0);
            right.topMargin = wide ? 0 : dp(15);
            categories.setLayoutParams(right);
        }
        super.onMeasure(width, height);
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}

