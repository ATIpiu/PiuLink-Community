package com.limelight.preferences;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;
import com.limelight.BuildConfig;
import com.limelight.R;
import mobile.Mobile;
import org.json.JSONObject;
import java.io.File;
import java.io.FileInputStream;
import java.io.OutputStream;

public class UpdateActivity extends Activity {
    private TextView status;
    private Button action;
    private File verified;
    private final String channel = BuildConfig.UPDATE_CHANNEL;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle(R.string.check_for_update);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = (int)(24 * getResources().getDisplayMetrics().density);
        root.setPadding(pad,pad,pad,pad);
        status = new TextView(this);
        action = new Button(this);
        root.addView(status); root.addView(action); setContentView(root);
        if (!installStatus(getIntent())) check();
    }
    private void busy(String text) { status.setText(text); action.setVisibility(android.view.View.GONE); }
    private void offer(String text, String label, Runnable next) {
        if (isFinishing() || isDestroyed()) return;
        status.setText(text); action.setText(label); action.setVisibility(android.view.View.VISIBLE);
        action.setOnClickListener(v -> next.run());
    }
    private void failed(Exception error) {
        runOnUiThread(() -> offer(getString(R.string.update_failed, error.getLocalizedMessage()), getString(R.string.update_retry), this::check));
    }
    private void check() {
        busy(getString(R.string.update_checking));
        new Thread(() -> {
            try {
                String json = Mobile.checkUpdate(channel, BuildConfig.VERSION_NAME);
                if ("null".equals(json)) runOnUiThread(() -> offer(getString(R.string.update_current), getString(android.R.string.ok), this::finish));
                else {
                    String version = new JSONObject(json).getString("version");
                    runOnUiThread(() -> offer(getString(R.string.update_available, version), getString(R.string.update_download), this::download));
                }
            } catch (Exception e) { failed(e); }
        }, "PiuLink update check").start();
    }
    private void download() {
        busy(getString(R.string.update_preparing));
        new Thread(() -> {
            try {
                JSONObject result = new JSONObject(Mobile.prepareUpdate(channel, BuildConfig.VERSION_NAME,
                        new File(getCacheDir(), "updates").getAbsolutePath(), getApplicationInfo().sourceDir));
                File apk = new File(result.getString("path"));
                String text = getString(R.string.update_verified,
                        result.getLong("downloaded") / 1048576.0, result.getLong("reused") / 1048576.0);
                runOnUiThread(() -> { verified = apk; offer(text, getString(R.string.update_install), this::install); });
            } catch (Exception e) { failed(e); }
        }, "PiuLink update download").start();
    }
    private void install() {
        if (!getPackageManager().canRequestPackageInstalls()) {
            startActivity(new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + getPackageName())));
            return;
        }
        busy(getString(R.string.update_installing));
        new Thread(() -> {
            PackageInstaller installer = getPackageManager().getPackageInstaller();
            int id = -1;
            try {
                PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
                params.setAppPackageName(getPackageName());
                id = installer.createSession(params);
                try (PackageInstaller.Session session = installer.openSession(id)) {
                    try (FileInputStream in = new FileInputStream(verified); OutputStream out = session.openWrite("base.apk", 0, verified.length())) {
                        byte[] buf = new byte[65536]; int n;
                        while ((n = in.read(buf)) != -1) out.write(buf, 0, n);
                        session.fsync(out);
                    }
                    Intent result = new Intent(this, UpdateActivity.class).setAction("piulink.update.INSTALL_RESULT")
                            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
                    session.commit(PendingIntent.getActivity(this, id, result, PendingIntent.FLAG_UPDATE_CURRENT |
                            (android.os.Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0)).getIntentSender());
                }
            } catch (Exception e) { if (id != -1) installer.abandonSession(id); failed(e); }
        }, "PiuLink package installer").start();
    }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); installStatus(intent); }
    private boolean installStatus(Intent result) {
        if (!"piulink.update.INSTALL_RESULT".equals(result.getAction())) return false;
        int code = result.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
        if (code == PackageInstaller.STATUS_PENDING_USER_ACTION) {
            Intent confirm = result.getParcelableExtra(Intent.EXTRA_INTENT);
            if (confirm != null) startActivity(confirm);
        } else offer(code == PackageInstaller.STATUS_SUCCESS ? getString(R.string.update_done) : getString(R.string.update_incomplete),
                getString(android.R.string.ok), this::finish);
        return true;
    }
}
