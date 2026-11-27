package com.limelight.piulink;

import android.app.Activity;
import android.graphics.drawable.GradientDrawable;
import android.view.*;
import android.widget.*;
import com.limelight.R;

/** One Activity, one stationary bar, and retained content for each destination. */
public final class HomeTabs {
    private final Activity activity;
    private final boolean touch;
    private final View[] pages = new View[2];
    private final LinearLayout[] tabs = new LinearLayout[pages.length];
    private final ImageView[] icons = new ImageView[pages.length];
    private final TextView[] labels = new TextView[pages.length];
    public final SettingsPanel settings;
    private int selected = -1;

    public HomeTabs(Activity activity) {
        this.activity = activity;
        ViewGroup content = activity.findViewById(android.R.id.content);
        pages[0] = content.getChildAt(0);
        content.removeView(pages[0]);
        LinearLayout shell = new LinearLayout(activity);
        android.content.res.Configuration config = activity.getResources().getConfiguration();
        touch = com.limelight.utils.UiHelper.isTouchscreenClient(activity);
        boolean rail = touch && config.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE
                && config.screenWidthDp >= 600;
        shell.setOrientation(rail ? LinearLayout.HORIZONTAL : LinearLayout.VERTICAL);
        shell.setBackgroundColor(activity.getResources().getColor(R.color.piulink_surface));
        FrameLayout deck = new FrameLayout(activity);
        settings = new SettingsPanel(activity);
        pages[1] = settings;
        for (View page : pages) deck.addView(page, new FrameLayout.LayoutParams(-1, -1));
        LinearLayout bar = new LinearLayout(activity);
        bar.setOrientation(rail ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL);
        if (rail) {
            shell.addView(bar, new LinearLayout.LayoutParams(dp(96), -1));
            ImageView mark = new ImageView(activity);
            mark.setImageResource(R.drawable.ic_piulink_mark);
            mark.setColorFilter(activity.getColor(R.color.home_ink));
            mark.setContentDescription(activity.getString(R.string.piulink_home_title));
            mark.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_YES);
            mark.setScaleType(ImageView.ScaleType.FIT_CENTER);
            mark.setPadding(dp(23), dp(16), dp(23), dp(16));
            bar.addView(mark, new LinearLayout.LayoutParams(-1, dp(78)));
            View edge = new View(activity);
            edge.setBackgroundColor(activity.getColor(R.color.home_line));
            shell.addView(edge, new LinearLayout.LayoutParams(dp(1), -1));
            shell.addView(deck, new LinearLayout.LayoutParams(0, -1, 1));
        } else {
            shell.addView(deck, new LinearLayout.LayoutParams(-1, 0, 1));
            bar.setElevation(dp(2));
        }
        bar.setBackgroundColor(activity.getResources().getColor(R.color.piulink_surface));
        int[] titles = {R.string.nav_computers, R.string.settings_label};
        int[] images = {R.drawable.ic_computer, R.drawable.ic_settings};
        android.util.TypedValue ripple = new android.util.TypedValue();
        activity.getTheme().resolveAttribute(android.R.attr.selectableItemBackground, ripple, true);
        for (int i = 0; i < pages.length; i++) {
            final int index = i;
            tabs[i] = new LinearLayout(activity);
            tabs[i].setOrientation(LinearLayout.VERTICAL);
            tabs[i].setGravity(Gravity.CENTER);
            tabs[i].setBackgroundResource(ripple.resourceId);
            tabs[i].setFocusable(true);
            tabs[i].setContentDescription(activity.getString(titles[i]));
            tabs[i].setOnClickListener(v -> select(index));
            icons[i] = new ImageView(activity);
            icons[i].setImageResource(images[i]);
            icons[i].setPadding(dp(16), dp(4), dp(16), dp(4));
            icons[i].setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            tabs[i].addView(icons[i], new LinearLayout.LayoutParams(dp(56), dp(32)));
            labels[i] = new TextView(activity);
            labels[i].setText(titles[i]);
            labels[i].setTextSize(12);
            labels[i].setGravity(Gravity.CENTER);
            labels[i].setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
            tabs[i].addView(labels[i], new LinearLayout.LayoutParams(-1, -2));
            bar.addView(tabs[i], rail ? new LinearLayout.LayoutParams(-1, dp(72))
                    : new LinearLayout.LayoutParams(0, -1, 1));
        }
        if (!rail) shell.addView(bar, new LinearLayout.LayoutParams(-1, dp(touch ? 64 : 72)));
        shell.setOnApplyWindowInsetsListener((view, insets) -> {
            view.setPadding(0, 0, 0, Math.max(0, insets.getSystemWindowInsetBottom() - content.getPaddingBottom()));
            View decor = activity.getWindow().getDecorView();
            int lightBars = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
            decor.setSystemUiVisibility(touch ? decor.getSystemUiVisibility() & ~lightBars
                    : decor.getSystemUiVisibility() | lightBars);
            return insets;
        });
        content.addView(shell, new ViewGroup.LayoutParams(-1, -1));
        shell.requestApplyInsets();
        select(0);
    }

    public int selected() { return selected; }
    public void select(int index) {
        index = index >= 0 && index < pages.length ? index : 0;
        if (index == selected) return;
        int previous = selected;
        selected = index;
        for (int i = 0; i < pages.length; i++) {
            boolean active = i == selected;
            pages[i].setVisibility(active ? View.VISIBLE : View.GONE);
            tabs[i].setSelected(active);
            int color = activity.getResources().getColor(active ? R.color.piulink_blue : R.color.piulink_text_secondary);
            icons[i].setColorFilter(color);
            labels[i].setTextColor(color);
            GradientDrawable pill = new GradientDrawable();
            pill.setColor(activity.getResources().getColor(active ? R.color.piulink_blue_soft : android.R.color.transparent));
            pill.setCornerRadius(dp(18));
            icons[i].setBackground(pill);
        }
        if (selected == 1) settings.refreshOverview();
        if (touch && previous >= 0) MobilePageMotion.enter(pages[selected], Integer.compare(selected, previous));
        if (selected == 0 && previous != 0 && activity instanceof com.limelight.PcView) {
            ((com.limelight.PcView) activity).refreshHomePreferences();
        }
    }
    public boolean back() {
        if (selected == 1 && settings.back()) return true;
        if (selected != 0) { select(0); return true; }
        return false;
    }
    public void refresh() {
        if (selected == 1) settings.reload();
    }
    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }
}
