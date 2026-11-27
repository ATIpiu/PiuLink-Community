package com.limelight.piulink;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Input routing and demand hints scoped to one foreground stream. All UI state is owned by the main thread. */
public final class SessionInputHints {
    public interface Listener {
        boolean canShowKeyboard();
        void showKeyboard();
        void microphoneUse(boolean active);
        void viewportPosition(float x, float y, boolean caret, long requestedAt);
    }
    private final Context context;
    private final String host, session;
    private final Listener listener;
    private final java.security.cert.X509Certificate certificate;
    private com.limelight.nvstream.http.NvHTTP paired;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private int click, generation;
    private boolean running, unsupported, checkingText;
    private long clickedAt;
    private boolean trackCaret, trackPointer, geometryUnsupported;
    private int geometryGeneration;

    public SessionInputHints(Context context, String host, String session, java.security.cert.X509Certificate certificate, Listener listener) {
        this.context = context.getApplicationContext();
        this.certificate = certificate;
        this.host = host; this.session = session; this.listener = listener;
    }

    public void setRunning(boolean active) {
        if (running == active || unsupported) return;
        running = active;
        generation++;
        resetViewportPoll();
        cancelTap();
        ui.removeCallbacks(poll);
        if (active) ui.post(poll);
        else listener.microphoneUse(false);
    }

    public void close() {
        setRunning(false);
        ui.removeCallbacksAndMessages(null);
        worker.shutdownNow();
    }

    public void cancelTap() { click++; pendingTap = false; ui.removeCallbacks(textProbe); }

    public void tapped() {
        cancelTap();
        if (!running || unsupported || !listener.canShowKeyboard()) return;
        pendingTap = true;
        clickedAt = SystemClock.elapsedRealtime();
        ui.postDelayed(textProbe, 150);
    }

    private com.limelight.nvstream.http.NvHTTP paired() throws Exception {
        if (paired == null) paired = new com.limelight.nvstream.http.NvHTTP(
                    new com.limelight.nvstream.http.ComputerDetails.AddressTuple(host, 47989), 47984, "",
                    certificate, com.limelight.binding.PlatformBinding.getCryptoProvider(context));
        return paired;
    }

    public void trackViewport(boolean caret, boolean pointer) {
        if (trackCaret == caret && trackPointer == pointer) return;
        trackCaret = caret;
        trackPointer = pointer;
        resetViewportPoll();
    }

    public void resetViewportPoll() {
        geometryGeneration++;
        ui.removeCallbacks(geometryPoll);
        if (running && !geometryUnsupported && (trackCaret || trackPointer)) ui.post(geometryPoll);
    }

    private final Runnable geometryPoll = new Runnable() {
        public void run() {
            final int epoch = geometryGeneration;
            final boolean caret = trackCaret;
            worker.execute(() -> {
                org.json.JSONObject point = null;
                long requestedAt = SystemClock.uptimeMillis();
                boolean unsupported = false;
                try {
                    point = DisplayInventoryClient.inputGeometry(context, host, session,
                            caret ? "viewportCaret" : "viewportPointer", paired()).optJSONObject(caret ? "caret" : "pointer");
                } catch (DisplayInventoryClient.SessionError ex) {
                    unsupported = "invalid_request".equals(ex.reason) || "unsupported".equals(ex.reason);
                } catch (Exception ignored) { }
                final boolean unavailable = unsupported;
                final float x = point == null ? Float.NaN : (float)point.optDouble("x");
                final float y = point == null ? Float.NaN : (float)point.optDouble("y");
                ui.post(() -> {
                    if (epoch != geometryGeneration || !running) return;
                    listener.viewportPosition(x, y, caret, requestedAt);
                    geometryUnsupported = unavailable;
                    if (!unavailable) ui.postDelayed(this, 160);
                });
            });
        }
    };

    private Boolean read(String action) {
        try {
            return DisplayInventoryClient.inputStatus(context, host, session, action, paired());
        }
        catch (DisplayInventoryClient.SessionError error) {
            if ("unsupported".equals(error.reason)) ui.post(() -> {
                setRunning(false);
                unsupported = true;
            });
        } catch (Exception ignored) { /* Transient loss: manual controls remain available. */ }
        return null;
    }

    private final Runnable textProbe = new Runnable() {
        public void run() {
            if (!running || checkingText || !listener.canShowKeyboard()) return;
            final int request = click;
            checkingText = true;
            worker.execute(() -> {
                Boolean editable = read("textFocus");
                ui.post(() -> {
                    checkingText = false;
                    if (!running || SystemClock.elapsedRealtime() - clickedAt > 1500) return;
                    if (click != request) {
                        if (pendingTap) { ui.removeCallbacks(this); ui.post(this); }
                    } else if (Boolean.TRUE.equals(editable) && listener.canShowKeyboard()) {
                        listener.showKeyboard();
                    }
                });
            });
        }
    };
    private boolean pendingTap;

    private final Runnable poll = new Runnable() {
        public void run() {
            if (!running) return;
            int epoch = generation;
            worker.execute(() -> {
                Boolean active = read("virtualMicrophoneUse");
                ui.post(() -> {
                    if (!running || generation != epoch) return;
                    listener.microphoneUse(Boolean.TRUE.equals(active));
                    ui.postDelayed(this, active == null ? 3000 : 1200);
                });
            });
        }
    };
}
