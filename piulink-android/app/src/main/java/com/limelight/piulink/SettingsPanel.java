package com.limelight.piulink;

import android.app.Activity;
import android.os.Bundle;
import android.view.KeyEvent;
import android.view.View;
import android.widget.*;
import com.limelight.R;
import com.limelight.preferences.StreamSettings;

/** Settings hierarchy hosted inside the home screen's fixed tabs. */
public final class SettingsPanel extends LinearLayout {
    private final Activity activity;
    private final TextView title;
    private final View back;
    private final boolean touch;
    private final TextView displayHint;
    private MobileSettingsOverview mobile;
    private final ScrollView overview;
    private final FrameLayout detail;
    private final LinearLayout detailBody;
    private final ScrollView sectionNavigation;
    private final TextView[] destinations = new TextView[KEYS.length];
    private final View[] sectionRows = new View[KEYS.length];
    private ListView settingsList;
    private int section = -1;
    private int renderedSection = -1;
    private static final String[] KEYS = {"category_basic_settings", "category_audio_settings",
            "category_gamepad_settings", "category_input_settings", "category_onscreen_controls",
            "category_host_settings", "category_ui_settings", "category_advanced_settings"};
    private static final int[] TITLES = {R.string.category_basic_settings, R.string.category_audio_settings,
            R.string.category_gamepad_settings, R.string.category_input_settings,
            R.string.category_on_screen_controls_settings, R.string.category_host_settings,
            R.string.category_ui_settings, R.string.category_advanced_settings};

    public SettingsPanel(Activity activity) {
        super(activity);
        this.activity = activity;
        touch = com.limelight.utils.UiHelper.isTouchscreenClient(activity);
        setOrientation(VERTICAL);
        setGravity(android.view.Gravity.CENTER_HORIZONTAL);
        int padding = dp(touch ? 20 : 16);
        setPadding(padding, dp(14), padding, 0);
        setBackgroundColor(getResources().getColor(R.color.piulink_background));
        LinearLayout header = new LinearLayout(activity);
        header.setGravity(android.view.Gravity.CENTER_VERTICAL);
        if (touch) {
            ImageButton arrow = new ImageButton(activity);
            arrow.setImageResource(R.drawable.ic_back);
            arrow.setContentDescription(activity.getString(R.string.navigate_back));
            android.util.TypedValue ripple = new android.util.TypedValue();
            activity.getTheme().resolveAttribute(android.R.attr.selectableItemBackgroundBorderless, ripple, true);
            arrow.setBackgroundResource(ripple.resourceId);
            header.addView(arrow, new LayoutParams(dp(48), dp(48)));
            back = arrow;
        } else {
            Button textBack = new Button(activity);
            textBack.setText(R.string.navigate_back);
            textBack.setAllCaps(false);
            header.addView(textBack);
            back = textBack;
        }
        back.setOnClickListener(v -> back());
        back.setOnKeyListener((v, keyCode, event) -> {
            if (keyCode == KeyEvent.KEYCODE_DPAD_DOWN &&
                    event.getAction() == KeyEvent.ACTION_DOWN && settingsList != null) {
                settingsList.requestFocus();
                if (settingsList.getSelectedItemPosition() < 0) settingsList.setSelection(0);
                return true;
            }
            return false;
        });
        title = new TextView(activity);
        title.setTextSize(28);
        title.setTextColor(getResources().getColor(R.color.piulink_text_primary));
        title.setPadding(0, 0, 0, padding / 2);
        header.addView(title, new LinearLayout.LayoutParams(0, -2, 1));
        if (touch) {
            title.setTypeface(android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL));
            header.setPadding(0, 0, 0, dp(12));
        }
        addView(header);
        displayHint = new TextView(activity);
        displayHint.setText(R.string.settings_detail_display_hint);
        displayHint.setTextSize(13);
        displayHint.setTextColor(activity.getResources().getColor(R.color.piulink_text_secondary));
        displayHint.setPadding(0, 0, 0, dp(12));
        addView(displayHint);
        overview = new ScrollView(activity);
        overview.setVerticalScrollBarEnabled(false);
        if (!touch) createClassicOverview(padding);
        addView(overview, new LinearLayout.LayoutParams(-1, 0, 1));
        detailBody = new LinearLayout(activity);
        detailBody.setBaselineAligned(false);
        sectionNavigation = new ScrollView(activity);
        sectionNavigation.setVerticalScrollBarEnabled(false);
        sectionNavigation.setVisibility(GONE);
        if (touch) {
            LinearLayout navigationItems = new LinearLayout(activity);
            navigationItems.setOrientation(VERTICAL);
            sectionNavigation.addView(navigationItems);
            for (int i = 0; i < KEYS.length; i++) {
                int index = i;
                TextView item = new TextView(activity);
                item.setText(MobileSettingsSection.forKey(KEYS[i]).title);
                item.setTextColor(activity.getColor(R.color.piulink_text_primary));
                item.setTextSize(15);
                item.setMinHeight(dp(48));
                item.setGravity(android.view.Gravity.START | android.view.Gravity.CENTER_VERTICAL);
                item.setPaddingRelative(dp(15), dp(8), dp(8), dp(8));
                item.setBackgroundResource(R.drawable.bg_detail_navigation);
                item.setFocusable(true);
                item.setOnClickListener(v -> { if (section != index) select(index); });
                navigationItems.addView(item, new LayoutParams(-1, -2));
                destinations[i] = item;
            }
            LayoutParams nav = new LayoutParams(dp(160), -1);
            nav.setMarginEnd(dp(26));
            detailBody.addView(sectionNavigation, nav);
        }
        detail = new FrameLayout(activity);
        detail.setId(R.id.home_settings_content);
        detailBody.addView(detail, new LayoutParams(0, -1, 1));
        addView(detailBody, new LayoutParams(-1, 0, 1));
        update();
    }

    private void createClassicOverview(int padding) {
        LinearLayout rows = new LinearLayout(activity);
        rows.setOrientation(VERTICAL);
        overview.addView(rows);
        TextView hint = new TextView(activity);
        hint.setText(R.string.settings_overview_hint);
        hint.setTextColor(getResources().getColor(R.color.piulink_text_secondary));
        hint.setPadding(0, 0, 0, padding);
        rows.addView(hint);
        row(rows, R.string.setup_guide_title, () -> activity.startActivity(
                new android.content.Intent(activity, SetupActivity.class)));
        row(rows, R.string.open_tailscale, () -> com.limelight.utils.TailscaleLauncher.openAppOrPlayStore(activity));
        for (int i = 0; i < KEYS.length; i++) {
            final int target = i;
            if (i == 4 && !activity.getPackageManager().hasSystemFeature(
                    android.content.pm.PackageManager.FEATURE_TOUCHSCREEN)) continue;
            sectionRows[i] = row(rows, TITLES[i], () -> select(target));
        }
    }

    private TextView row(LinearLayout parent, int label, Runnable action) {
        TextView row = new TextView(activity);
        row.setText(activity.getString(label).trim());
        row.setCompoundDrawablesRelativeWithIntrinsicBounds(0, 0, R.drawable.ic_section_next, 0);
        row.setGravity(android.view.Gravity.CENTER_VERTICAL | android.view.Gravity.START);
        row.setTextSize(16);
        int pad = Math.round(14 * getResources().getDisplayMetrics().density);
        row.setMinHeight(Math.round(56 * getResources().getDisplayMetrics().density));
        row.setPadding(pad / 2, pad, pad / 2, pad);
        row.setBackgroundResource(R.drawable.bg_settings_section);
        row.setFocusable(true);
        row.setOnClickListener(v -> action.run());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        parent.addView(row, params);
        return row;
    }

    public int getSection() { return section; }
    public void select(int value) {
        int previous = section;
        section = value >= 0 && value < KEYS.length ? value : -1;
        update();
        if (section >= 0) reload();
        if (touch && section >= 0 && previous != section) MobilePageMotion.enter(detail, 1);
    }
    void refreshOverview() {
        if (!touch || section >= 0) return;
        if (mobile == null) {
            mobile = new MobileSettingsOverview(activity, this::select, sectionRows);
            overview.addView(mobile);
        }
        mobile.refresh();
    }

    public void reload() {
        if (activity.isFinishing() || activity.isDestroyed()) return;
        if (section < 0) { refreshOverview(); return; }
        android.app.Fragment previous = activity.getFragmentManager().findFragmentByTag("home_settings");
        ListView previousList = previous != null && previous.getView() != null
                ? previous.getView().findViewById(android.R.id.list) : null;
        android.os.Parcelable position = renderedSection == section && previousList != null
                ? previousList.onSaveInstanceState() : null;
        renderedSection = section;
        StreamSettings.SettingsFragment fragment = new StreamSettings.SettingsFragment();
        Bundle args = new Bundle();
        args.putString("selected_section", KEYS[section]);
        fragment.setArguments(args);
        activity.getFragmentManager().beginTransaction()
                .replace(R.id.home_settings_content, fragment, "home_settings").commitAllowingStateLoss();
        activity.getFragmentManager().executePendingTransactions();
        if (fragment.getView() != null) {
            settingsList = fragment.getView().findViewById(android.R.id.list);
            if (settingsList != null) {
                if (touch) {
                    settingsList.setDivider(null);
                    settingsList.setSelector(android.R.color.transparent);
                    settingsList.setItemsCanFocus(true);
                    settingsList.setPadding(0, 0, 0, dp(14));
                } else settingsList.setSelector(R.drawable.bg_settings_list_focus);
                settingsList.setDrawSelectorOnTop(!touch);
                if (position != null) settingsList.onRestoreInstanceState(position);
                settingsList.setOnKeyListener((view, keyCode, event) -> {
                    if (keyCode == KeyEvent.KEYCODE_DPAD_UP &&
                            event.getAction() == KeyEvent.ACTION_DOWN && isListAtTop(settingsList)) {
                        back.requestFocus();
                        return true;
                    }
                    return false;
                });
                ListView list = settingsList;
                list.post(() -> {
                    if (list != settingsList || list.isInTouchMode()) return;
                    list.requestFocus();
                    if (position == null && list.getSelectedItemPosition() < 0) list.setSelection(0);
                });
            }
        }
    }

    private static boolean isListAtTop(ListView list) {
        int selected = list.getSelectedItemPosition();
        if (selected > 0) return false;
        if (list.getFirstVisiblePosition() > 0) return false;
        View first = list.getChildAt(0);
        return first == null || first.getTop() >= list.getPaddingTop();
    }

    public boolean back() {
        if (section < 0) return false;
        int previousSection = section;
        section = -1;
        settingsList = null;
        update();
        refreshOverview();
        if (touch) MobilePageMotion.enter(overview, -1);
        View previousRow = sectionRows[previousSection];
        if (previousRow != null && !previousRow.isInTouchMode()) previousRow.post(previousRow::requestFocus);
        return true;
    }
    private void update() {
        boolean root = section < 0;
        int heading = root ? R.string.settings_label : touch ? MobileSettingsSection.forKey(KEYS[section]).title : TITLES[section];
        title.setText(activity.getString(heading).trim());
        back.setVisibility(root ? GONE : VISIBLE);
        title.setTextSize(touch ? (root ? 32 : 24) : 28);
        title.setPaddingRelative(root ? 0 : dp(6), 0, 0, touch ? 0 : getPaddingTop() / 2);
        displayHint.setVisibility(touch && section == 0 ? VISIBLE : GONE);
        overview.setVisibility(root ? VISIBLE : GONE);
        detailBody.setVisibility(root ? GONE : VISIBLE);
        if (touch) {
            for (int i = 0; i < destinations.length; i++) destinations[i].setSelected(i == section);
        }
    }

    @Override protected void onMeasure(int width, int height) {
        if (touch) {
            int available = Math.max(0, MeasureSpec.getSize(width) - getPaddingLeft() - getPaddingRight());
            for (int i = 0; i < getChildCount(); i++) getChildAt(i).getLayoutParams().width = Math.min(available, dp(920));
            sectionNavigation.setVisibility(available >= dp(680) && section >= 0 ? VISIBLE : GONE);
        }
        super.onMeasure(width, height);
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
}
