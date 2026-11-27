package com.limelight.piulink.ssh;
import android.app.Activity;
import android.os.Bundle;
public final class TerminalHub extends Activity {
    private TerminalPanel panel;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        com.limelight.utils.UiHelper.setLocale(this);
        panel = new TerminalPanel(this, state == null ? getIntent().getStringExtra("host") : null);
        setContentView(panel);
        com.limelight.utils.UiHelper.notifyNewRootView(this);
    }
    @Override protected void onResume() { super.onResume(); panel.refresh(); }
}