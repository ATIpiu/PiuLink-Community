package com.limelight.preferences;

import android.app.Activity;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.os.Bundle;
import android.view.Gravity;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.HorizontalScrollView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import com.limelight.LimeLog;
import com.limelight.R;
import com.limelight.utils.UiHelper;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;


public final class LogActivity extends Activity {
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private TextView logText;
    private Button older, newer, refresh, copy, clear;
    private ScrollView scroll;
    private LogPages pages;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        UiHelper.setLocale(this);
        setTitle(R.string.view_logs_title);

        int pad = Math.round(16 * getResources().getDisplayMetrics().density);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(pad, pad, pad, pad);
        root.setBackgroundColor(getResources().getColor(R.color.piulink_background));

        TextView title = new TextView(this);
        title.setText(R.string.view_logs_title);
        title.setTextSize(22);
        title.setTextColor(getResources().getColor(R.color.piulink_text_primary));
        root.addView(title);

        TextView summary = new TextView(this);
        summary.setText(R.string.view_logs_summary);
        summary.setTextColor(getResources().getColor(R.color.piulink_text_secondary));
        summary.setPadding(0, pad / 3, 0, pad);
        root.addView(summary);

        LinearLayout actions = new LinearLayout(this);
        actions.setOrientation(LinearLayout.HORIZONTAL);
        refresh = new Button(this);
        refresh.setText(R.string.logs_refresh);
        refresh.setOnClickListener(v -> loadLogs());
        copy = new Button(this);
        copy.setText(R.string.logs_copy);
        copy.setOnClickListener(v -> copyLogs());
        clear = new Button(this);
        clear.setText(R.string.logs_clear);
        clear.setOnClickListener(v -> clearLogs());
        actions.addView(refresh);
        actions.addView(copy);
        actions.addView(clear);
        root.addView(actions);
        Button upload = new Button(this);
        upload.setText(R.string.diag_upload);
        upload.setOnClickListener(v -> new android.app.AlertDialog.Builder(this)
                .setTitle(R.string.diag_upload_title)
                .setMessage(R.string.diag_upload_body)
                .setNegativeButton(android.R.string.cancel, null)
                .setPositiveButton(R.string.diag_upload_confirm, (dialog, which) -> {
                    upload.setEnabled(false);
                    worker.execute(() -> {
                        String msg;
                        try { msg = getString(R.string.diag_receipt) + "  " + com.limelight.DiagnosticReport.upload(getApplicationContext()); }
                        catch (Exception ex) { msg = getString(R.string.diag_upload_failed); }
                        final String result = msg;
                        runOnUiThread(() -> {
                            if (isFinishing() || isDestroyed()) return;
                            upload.setEnabled(true);
                            summary.setText(result);
                            summary.setTextIsSelectable(true);
                        });
                    });
                }).show());
        root.addView(upload);
        LinearLayout navigation = new LinearLayout(this);
        older = new Button(this);
        older.setText(R.string.logs_older);
        older.setOnClickListener(v -> readPage(-1, false));
        newer = new Button(this);
        newer.setText(R.string.logs_newer);
        newer.setOnClickListener(v -> readPage(1, false));
        navigation.addView(older);
        navigation.addView(newer);
        root.addView(navigation);

        logText = new TextView(this);
        logText.setTextSize(12);
        logText.setTextColor(getResources().getColor(R.color.piulink_text_primary));
        logText.setGravity(Gravity.START);
        logText.setTextIsSelectable(true);
        logText.setTypeface(android.graphics.Typeface.MONOSPACE);
        logText.setPadding(pad, pad, pad, pad);

        HorizontalScrollView horizontal = new HorizontalScrollView(this);
        horizontal.addView(logText, new HorizontalScrollView.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        scroll = new ScrollView(this);
        scroll.addView(horizontal);
        root.addView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));
        setContentView(root);
        UiHelper.notifyNewRootView(this);
        loadLogs();
    }

    private void loadLogs() { readPage(0, false); }

    private void readPage(int direction, boolean truncate) {
        refresh.setEnabled(false);
        clear.setEnabled(false);
        copy.setEnabled(false);
        older.setEnabled(false);
        newer.setEnabled(false);
        worker.execute(() -> {
            String contents;
            boolean readable = false;
            try {
                if (truncate) {
                    for (File file : LimeLog.getLogFiles(this)) {
                        try (FileOutputStream output = new FileOutputStream(file, false)) { /* truncate in place */ }
                    }
                    LimeLog.info("Logs cleared by user");
                }
                if (direction == 0 || pages == null) pages = new LogPages(LimeLog.getLogFiles(this));
                pages.move(direction);
                String body = pages.read();
                readable = !body.isEmpty();
                contents = readable ? pages.files[pages.fileIndex].getName() + " · " + (pages.pageIndex + 1) + "\n\n" + body
                        : getString(R.string.logs_empty);
            } catch (IOException error) {
                contents = getString(R.string.logs_read_failed, error.getMessage());
            }
            final String text = contents;
            final boolean canCopy = readable;
            runOnUiThread(() -> {
                if (isDestroyed() || isFinishing()) return;
                logText.setText(text);
                scroll.scrollTo(0, 0);
                refresh.setEnabled(true);
                clear.setEnabled(true);
                copy.setEnabled(canCopy);
                older.setEnabled(pages != null && pages.hasOlder());
                newer.setEnabled(pages != null && pages.hasNewer());
            });
        });
    }

    @Override protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }

    private void copyLogs() {
        ClipboardManager clipboard = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
        clipboard.setPrimaryClip(ClipData.newPlainText("PiuLink logs", logText.getText()));
        Toast.makeText(this, R.string.logs_copied, Toast.LENGTH_SHORT).show();
    }

    private void clearLogs() { readPage(0, true); }
}
