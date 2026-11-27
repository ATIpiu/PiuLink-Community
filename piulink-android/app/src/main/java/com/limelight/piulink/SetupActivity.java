package com.limelight.piulink;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Bundle;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import com.limelight.R;
import com.limelight.preferences.AddComputerManually;
import com.limelight.utils.HelpLauncher;
import com.limelight.utils.TailscaleLauncher;
import com.limelight.utils.UiHelper;

/** Resumable preparation guide. Installation is never treated as proof of connectivity. */
public final class SetupActivity extends Activity {
    private static final int ADD_COMPUTER = 1;
    private SharedPreferences state;
    private int step;
    private Button primaryAction;

    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        UiHelper.setLocale(this);
        getWindow().setStatusBarColor(getColor(R.color.piulink_background));
        getWindow().setNavigationBarColor(getColor(R.color.piulink_background));
        View decor = getWindow().getDecorView();
        int lightBars = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        decor.setSystemUiVisibility(UiHelper.isTouchscreenClient(this) ? decor.getSystemUiVisibility() & ~lightBars
                : decor.getSystemUiVisibility() | lightBars);
        state = getSharedPreferences("connection_setup", MODE_PRIVATE);
        step = Math.max(0, Math.min(2, state.getInt("step", 0)));
    }

    @Override public void onResume() {
        super.onResume();
        render();
    }

    private void advance(int next) {
        step = next;
        state.edit().putInt("step", step).apply();
        render();
    }

    private void render() {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(getColor(R.color.piulink_background));
        LinearLayout body = new FormColumn(this, null);
        body.setOrientation(LinearLayout.VERTICAL);
        body.setPadding(dp(24), dp(26), dp(24), dp(20));
        scroll.addView(body, new android.widget.FrameLayout.LayoutParams(-1, -2, android.view.Gravity.TOP | android.view.Gravity.CENTER_HORIZONTAL));
        text(body, getString(R.string.setup_progress, step + 1), 14);
        text(body, getString(new int[]{R.string.setup_pc_title, R.string.setup_ts_title,
                R.string.setup_pair_title}[step]), 28);
        text(body, getString(new int[]{R.string.setup_pc_body, R.string.guide_remote_routes,
                R.string.guide_pair_routes}[step]), 16);
        if (step == 0) {
            button(body, R.string.setup_lan_discover, true, v -> showComputers());
            button(body, R.string.setup_pc_ready, v -> advance(1));
        } else if (step == 1) {
            boolean installed = TailscaleLauncher.isInstalled(this);
            button(body, R.string.guide_enter_invite, true, v -> enterAddress());
            button(body, installed ? R.string.setup_ts_open : R.string.setup_ts_install,
                    v -> TailscaleLauncher.openAppOrPlayStore(this));
            if (!installed) button(body, R.string.setup_ts_download,
                    v -> HelpLauncher.launchUrl(this, "https://tailscale.com/download/android"));
            button(body, R.string.setup_ts_next, v -> advance(2));
            button(body, R.string.setup_lan_discover, v -> showComputers());
        } else {
            button(body, R.string.guide_enter_address, true, v -> enterAddress());
            button(body, R.string.setup_show_computers, v -> showComputers());
        }
        if (step > 0) button(body, R.string.setup_previous, v -> advance(step - 1));
        button(body, R.string.setup_later, v -> finish());
        setContentView(scroll);
        UiHelper.notifyNewRootView(this);
        if (UiHelper.isTelevision(this) && primaryAction != null) primaryAction.requestFocus();
    }

    private void enterAddress() {
        startActivityForResult(new Intent(this, AddComputerManually.class), ADD_COMPUTER);
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == ADD_COMPUTER && result == RESULT_OK) showComputers();
    }

    private Intent computersIntent() {
        return new Intent(this, com.limelight.PcView.class)
                .setAction(com.limelight.PcView.ACTION_SHOW_COMPUTERS)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP);
    }

    private void showComputers() {
        startActivity(computersIntent());
        finish();
    }

    @Override public void onBackPressed() {
        if (step > 0) advance(step - 1);
        else super.onBackPressed();
    }

    private void text(LinearLayout body, String value, int size) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(size);
        boolean title = size == 28;
        view.setTextColor(getColor(size == 14 ? R.color.piulink_text_secondary : R.color.piulink_text_primary));
        if (title) view.setTypeface(android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL));
        view.setLineSpacing(dp(2), 1f);
        view.setPadding(0, 0, 0, dp(title ? 14 : size == 14 ? 12 : 22));
        body.addView(view);
    }

    private void button(LinearLayout body, int label, View.OnClickListener action) {
        button(body, label, false, action);
    }

    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }

    private void button(LinearLayout body, int label, boolean primary, View.OnClickListener action) {
        Button button = new Button(this);
        if (primary) primaryAction = button;
        button.setText(label);
        button.setAllCaps(false);
        button.setMinHeight(dp(48));
        button.setTextSize(16);
        button.setLetterSpacing(0);
        button.setBackgroundTintList(null);
        boolean touchPrimary = primary && !UiHelper.isTelevision(this);
        button.setBackgroundResource(touchPrimary ? R.drawable.bg_dialog_primary : R.drawable.bg_dialog_choice);
        button.setTextColor(getColor(touchPrimary ? R.color.piulink_on_accent : R.color.piulink_text_primary));
        button.setStateListAnimator(null);
        button.setPadding(dp(14), dp(10), dp(14), dp(10));
        button.setOnClickListener(action);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.bottomMargin = dp(10);
        body.addView(button, params);
    }
}
