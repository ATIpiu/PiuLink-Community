package com.limelight.piulink.ssh;

import android.app.Activity;
import com.limelight.R;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.view.View;
import android.widget.Button;
import android.widget.ProgressBar;
import android.widget.TextView;

import com.limelight.LimeLog;
import com.limelight.computers.ComputerDatabaseManager;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.piulink.transfer.FileTransferClient;
import com.limelight.piulink.transfer.TransferIo;
import com.limelight.piulink.transfer.TransferHostResolver;

import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Android Sharesheet target using independently paired file transfer. */
public final class TransferActivity extends Activity {
    public static final String EXTRA_LIBRARY_HOST = "com.limelight.piulink.transfer.LIBRARY_HOST";
    private static final int REQUEST_PAIR_COMPUTER = 1;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private TextView status;
    private TextView title;
    private TextView bytes;
    private Button close;
    private ProgressBar progress;
    private Button action;
    private List<Uri> pendingUris;
    private String pendingText;
    private String pendingTextName;
    private volatile boolean stopped;
    private volatile FileTransferClient activeTransfer;
    private volatile TransferHostResolver activeResolver;
    private volatile InputStream activeInput;
    private final java.util.Set<Uri> completedUris = java.util.concurrent.ConcurrentHashMap.newKeySet();
    private volatile boolean textCompleted;
    private String completedDestination;
    private String destinationName;
    private boolean running;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        pendingTextName = state == null ? null : state.getString("pendingTextName");
        if (pendingTextName == null) pendingTextName = "PiuLink-text-" +
                new SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(new Date()) + ".txt";
        com.limelight.utils.UiHelper.setLocale(this);
        LimeLog.infoEvent("transfer.android.share_opened", "file-transfer", "share target opened", null, null);
        buildUi();
        if (android.os.Build.VERSION.SDK_INT >= 33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS}, 48012);
        else if (android.os.Build.VERSION.SDK_INT <= 28 && checkSelfPermission(android.Manifest.permission.WRITE_EXTERNAL_STORAGE) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{android.Manifest.permission.WRITE_EXTERNAL_STORAGE}, 48012);
        try { readShareIntent(getIntent()); }
        catch (RuntimeException error) { LimeLog.errEvent("transfer.android.share_parse_failed", "file-transfer", "unable to parse share intent", error, null, null); fail("无法读取分享内容，请重新选择文件。"); return; }
        if (state != null) {
            ArrayList<String> completed = state.getStringArrayList("completedUris");
            if (completed != null) for (String value : completed) completedUris.add(Uri.parse(value));
            textCompleted = state.getBoolean("textCompleted", false);
            completedDestination = state.getString("completedDestination");
            destinationName = state.getString("destinationName");
            if (destinationName != null) title.setText(getString(R.string.transfer_send_to, destinationName));
            if (totalCount() != 0 && confirmedCount() == totalCount()) { showComplete(); return; }
            fail("传输页面已恢复。请先检查电脑下载目录；未收到确认的文件可能已经保存。", "继续剩余文件", this::chooseDestination);
        } else chooseDestination();
    }

    private void buildUi() {
        setContentView(R.layout.activity_file_transfer);
        title = findViewById(R.id.transferTitle);
        status = findViewById(R.id.transferStatus);
        bytes = findViewById(R.id.transferBytes);
        progress = findViewById(R.id.transferProgress);
        progress.setIndeterminate(true);
        action = findViewById(R.id.transferAction);
        close = findViewById(R.id.transferClose);
        close.setOnClickListener(v -> finish());
        sizeCard(getResources().getConfiguration());
    }

    @Override public void onConfigurationChanged(android.content.res.Configuration configuration) {
        super.onConfigurationChanged(configuration);
        sizeCard(configuration);
    }

    private void sizeCard(android.content.res.Configuration configuration) {
        View card = findViewById(R.id.transferCard);
        android.view.ViewGroup.LayoutParams params = card.getLayoutParams();
        params.width = configuration.screenWidthDp >= 600
                ? Math.round(520 * getResources().getDisplayMetrics().density)
                : android.view.ViewGroup.LayoutParams.MATCH_PARENT;
        card.setLayoutParams(params);
    }

    private void readShareIntent(Intent intent) {
        java.util.LinkedHashSet<Uri> uris = new java.util.LinkedHashSet<>();
        ClipData clip = intent.getClipData();
        if (clip != null) for (int i = 0; i < clip.getItemCount(); i++) addUri(uris, clip.getItemAt(i).getUri());
        if (Intent.ACTION_SEND_MULTIPLE.equals(intent.getAction())) {
            ArrayList<android.os.Parcelable> streams = intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (streams != null) for (android.os.Parcelable stream : streams)
                if (stream instanceof Uri) addUri(uris, (Uri)stream);
        } else {
            android.os.Parcelable stream = intent.getParcelableExtra(Intent.EXTRA_STREAM);
            if (stream instanceof Uri) addUri(uris, (Uri)stream);
        }
        pendingUris = new ArrayList<>(uris);
        CharSequence text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT);
        pendingText = text == null || text.length() == 0 ? null : text.toString();
    }

    private static void addUri(java.util.Set<Uri> uris, Uri uri) {
        // Exported activity: accept only content URIs for which Android granted access.
        if (uri != null && "content".equalsIgnoreCase(uri.getScheme())) uris.add(uri);
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == REQUEST_PAIR_COMPUTER && !stopped) {
            fail("返回后可重新选择接收电脑。", "选择接收电脑", this::chooseDestination);
        }
    }

    private void chooseDestination() {
        List<Destination> profiles = new ArrayList<>();
        try (DatabaseHandle database = new DatabaseHandle(this)) {
            for (ComputerDetails computer : database.manager.getAllComputers()) {
                if (computer.serverCert != null && (computer.activeAddress != null || computer.manualAddress != null ||
                        computer.localAddress != null || computer.remoteAddress != null || computer.ipv6Address != null)) {
                    profiles.add(new Destination(computer));
                }
            }
        }
        catch (Exception e) { fail("无法读取电脑连接：" + e.getMessage()); return; }
        if (pendingUris.isEmpty() && (pendingText == null || pendingText.trim().isEmpty())) {
            fail("分享内容中没有可发送的文件或文本。");
            return;
        }
        // Only our own activity can preselect a recipient. External shares use the normal chooser.
        if (getPackageName().equals(getCallingPackage()) && getIntent().hasExtra(EXTRA_LIBRARY_HOST)) {
            String target = getIntent().getStringExtra(EXTRA_LIBRARY_HOST);
            for (Destination profile : profiles) {
                if (target != null && target.equals(profile.computer.uuid)) { upload(profile); return; }
            }
            fail(getString(R.string.transfer_target_missing));
            return;
        }
        if (profiles.isEmpty()) {
            fail("没有可接收文件的已配对电脑。配对后返回这里，可继续发送。", "去配对电脑", () ->
                    startActivityForResult(new Intent(this, com.limelight.PcView.class)
                            .setAction(com.limelight.PcView.ACTION_SHOW_COMPUTERS), REQUEST_PAIR_COMPUTER));
            return;
        }
        if (profiles.size() == 1) { upload(profiles.get(0)); return; }
        String[] labels = new String[profiles.size()];
        for (int i = 0; i < profiles.size(); i++) labels[i] = profiles.get(i).name;
        new AlertDialog.Builder(this).setTitle("发送到哪台电脑？")
                .setItems(labels, (dialog, which) -> upload(profiles.get(which)))
                .setOnCancelListener(dialog -> finish()).show();
    }

    private void upload(Destination profile) {
        if (running || stopped) return;
        final String operationId = java.util.UUID.randomUUID().toString().replace("-", "").substring(0, 12);
        final long started = android.os.SystemClock.elapsedRealtime();
        // A restored sharesheet can select a different host. ACKs belong to that destination only.
        String destination = profile.computer.uuid;
        if (destination == null || !destination.equals(completedDestination)) {
            completedUris.clear(); textCompleted = false;
        }
        completedDestination = destination;
        destinationName = profile.name;
        title.setText(getString(R.string.transfer_send_to, destinationName));
        running = true;
        LimeLog.infoEvent("transfer.android.batch_started", "file-transfer", "share batch started", operationId, LimeLog.fields("file_count", pendingUris.size(), "has_text", pendingText != null));
        progress.setVisibility(android.view.View.VISIBLE); progress.setIndeterminate(true); action.setVisibility(android.view.View.GONE);
        bytes.setVisibility(View.GONE);
        close.setText(R.string.transfer_cancel_close);
        status.setText("正在连接 " + profile.name + "…");
        worker.execute(() -> {
            try (TransferHostResolver resolver = new TransferHostResolver()) {
                activeResolver = resolver;
                if (stopped) return;
                String host = resolver.resolve(getApplicationContext(), profile.computer);
                try (FileTransferClient client = new FileTransferClient(this, host)) {
                    activeTransfer = client;
                    if (stopped) return;
                    client.prepareForSending(profile.computer,
                            () -> ui(() -> status.setText(getString(R.string.transfer_confirm_host, profile.name))));
                    int total = totalCount();
                    for (int index = 0; index < pendingUris.size(); index++) {
                        Uri uri = pendingUris.get(index);
                        if (completedUris.contains(uri)) continue;
                        String name = safeName(displayName(uri));
                        int current = index + 1;
                        ui(() -> showFile(name, current, total));
                        sendUri(client, uri, name);
                        completedUris.add(uri); // Only after the receiver acknowledges a committed file.
                    }
                    if (pendingText != null && !textCompleted) {
                        String name = pendingTextName;
                        byte[] text = pendingText.getBytes(StandardCharsets.UTF_8);
                        ui(() -> showFile(name, total, total));
                        try (InputStream input = new java.io.ByteArrayInputStream(text)) { send(client, name, text.length, input); }
                        textCompleted = true;
                    }
                    LimeLog.infoEvent("transfer.android.batch_completed", "file-transfer", "share batch completed", operationId, LimeLog.fields("elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
                    ui(this::showComplete);
                }
            } catch (Exception error) {
                LimeLog.errEvent("transfer.android.batch_failed", "file-transfer", "share batch failed", error, operationId, LimeLog.fields("elapsed_ms", android.os.SystemClock.elapsedRealtime() - started));
                ui(() -> fail(getString(R.string.transfer_batch_failed, confirmedCount(), totalCount(), message(error)),
                        getString(R.string.transfer_retry_remaining, totalCount() - confirmedCount()), () -> upload(profile)));
            } finally {
                activeTransfer = null; activeResolver = null;
                ui(() -> running = false);
            }
        });
    }

    private int totalCount() { return pendingUris.size() + (pendingText == null ? 0 : 1); }
    private int confirmedCount() { return completedUris.size() + (textCompleted ? 1 : 0); }

    private void showFile(String name, int current, int total) {
        status.setText(getString(R.string.transfer_file_position, current, total, name));
        progress.setIndeterminate(true);
        bytes.setVisibility(View.GONE);
    }

    private void send(FileTransferClient client, String name, long size, InputStream input) throws Exception {
        String fullSize = android.text.format.Formatter.formatFileSize(this, size);
        client.send(name, size, input, sent -> ui(() -> {
            progress.setIndeterminate(false);
            int percent = size == 0 ? 100 : (int)(sent * 100 / size);
            progress.setProgress(percent);
            bytes.setVisibility(View.VISIBLE);
            bytes.setText(sent == size ? getString(R.string.transfer_wait_save)
                    : getString(R.string.transfer_byte_progress, percent, android.text.format.Formatter.formatFileSize(this, sent), fullSize));
        }));
    }

    private void showComplete() {
        progress.setIndeterminate(false); progress.setProgress(100);
        bytes.setVisibility(View.GONE);
        close.setText(R.string.transfer_close);
        status.setText(getString(R.string.transfer_batch_saved, confirmedCount()));
    }

    private void sendUri(FileTransferClient client, Uri uri, String name) throws Exception {
        long size = contentSize(uri);
        if (size >= 0) TransferIo.validateSize(size);
        java.io.File staged = null;
        try {
            try (InputStream source = getContentResolver().openInputStream(uri)) {
                if (source == null) throw new java.io.IOException("无法读取 " + name);
                activeInput = source;
                if (stopped || Thread.currentThread().isInterrupted()) throw new java.io.InterruptedIOException("传输已取消");
                if (size < 0) {
                    ui(() -> { bytes.setVisibility(View.VISIBLE); bytes.setText(R.string.transfer_reading_source); });
                    staged = TransferIo.stageUnknown(source, getCacheDir());
                } else send(client, name, size, source);
            } finally { activeInput = null; }
            if (staged != null) {
                try (InputStream input = new java.io.FileInputStream(staged)) {
                    activeInput = input;
                    send(client, name, staged.length(), input);
                } finally { activeInput = null; }
            }
        } finally { if (staged != null && !staged.delete()) staged.deleteOnExit(); }
    }

    private long contentSize(Uri uri) {
        try (Cursor cursor = getContentResolver().query(uri, new String[]{OpenableColumns.SIZE}, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                int column = cursor.getColumnIndex(OpenableColumns.SIZE);
                if (column >= 0 && !cursor.isNull(column)) {
                    long size = cursor.getLong(column);
                    if (size >= 0) return size;
                }
            }
        } catch (RuntimeException ignored) { /* Provider metadata is optional; openInputStream still enforces grants. */ }
        return -1;
    }

    static String safeName(String value) { return TransferIo.safeName(value); }

    private void ui(Runnable action) { runOnUiThread(() -> { if (!stopped && !isFinishing()) action.run(); }); }

    private String displayName(Uri uri) {
        try (Cursor cursor = getContentResolver().query(uri, new String[]{OpenableColumns.DISPLAY_NAME}, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) return cursor.getString(0);
        } catch (Exception ignored) { }
        return uri.getLastPathSegment();
    }

    private static String message(Exception error) {
        String value = error.getMessage();
        return value == null || value.trim().isEmpty() ? error.getClass().getSimpleName() : value;
    }

    private void fail(String text) {
        progress.setVisibility(android.view.View.GONE);
        bytes.setVisibility(View.GONE);
        action.setVisibility(View.GONE);
        close.setText(R.string.transfer_close);
        status.setText(text);
    }

    private void fail(String text, String actionText, Runnable callback) {
        fail(text);
        action.setText(actionText);
        action.setVisibility(android.view.View.VISIBLE);
        action.setOnClickListener(v -> callback.run());
    }

    @Override protected void onSaveInstanceState(Bundle state) {
        state.putString("pendingTextName", pendingTextName);
        ArrayList<String> completed = new ArrayList<>();
        for (Uri uri : completedUris) completed.add(uri.toString());
        state.putStringArrayList("completedUris", completed);
        state.putBoolean("textCompleted", textCompleted);
        state.putString("completedDestination", completedDestination);
        state.putString("destinationName", destinationName);
        super.onSaveInstanceState(state);
    }

    @Override protected void onDestroy() {
        stopped = true;
        worker.shutdownNow();
        // Disconnect sockets and the provider stream as well as interrupting the worker.
        // A provider's close() can itself block: never wait for it on the UI thread.
        final FileTransferClient client = activeTransfer;
        final TransferHostResolver resolver = activeResolver;
        final InputStream source = activeInput;
        Thread cleanup = new Thread(() -> {
            if (client != null) client.close();
            if (resolver != null) resolver.close();
            try { if (source != null) source.close(); } catch (java.io.IOException ignored) { }
        }, "PiuLink transfer cancel");
        cleanup.setDaemon(true); cleanup.start();
        super.onDestroy();
    }

    private static final class Destination {
        final String name; final ComputerDetails computer;
        Destination(ComputerDetails computer) { this.name=computer.name; this.computer=computer; }
    }
    private static final class DatabaseHandle implements AutoCloseable { final ComputerDatabaseManager manager; DatabaseHandle(Activity activity) { manager=new ComputerDatabaseManager(activity); } @Override public void close() { manager.close(); } }
}
