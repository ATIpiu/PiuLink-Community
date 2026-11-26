package com.limelight;

import android.app.ActivityManager;
import android.app.ApplicationExitInfo;
import android.content.Context;
import android.os.Build;
import android.util.AtomicFile;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.TreeSet;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;

import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

/** Constructs a report from selected diagnostics. Never serialize the original log record. */
public final class DiagnosticReport {
    private static final String URL = "https://atipiu.org:8443/piulink/v1/diagnostics";
    private static final Pattern EVENT = Pattern.compile("diag\\.start|legacy\\.(message|warning|severe|debug)|(?:stream|transfer)\\.android\\.[a-z_]{1,40}");
    private static final Pattern SYMBOL = Pattern.compile("[A-Za-z_$][A-Za-z0-9_.$]{0,158}");
    private static AtomicFile crashFile(Context ctx) {
        return new AtomicFile(new File(ctx.getNoBackupFilesDir(), "diagnostic-crash.json"));
    }

    public static void install(Context ctx) {
        Thread.UncaughtExceptionHandler original = Thread.getDefaultUncaughtExceptionHandler();
        Thread.setDefaultUncaughtExceptionHandler((thread, error) -> {
            try { saveCrash(ctx, error); } catch (Throwable ignored) { }
            finally {
                if (original != null) original.uncaughtException(thread, error);
                else android.os.Process.killProcess(android.os.Process.myPid());
            }
        });
    }

    private static JSONObject entry(long time, String code) throws Exception {
        return new JSONObject().put("time", time).put("code", code);
    }

    private static synchronized void saveCrash(Context ctx, Throwable error) throws Exception {
        long time = System.currentTimeMillis() / 1000;
        JSONArray rows = new JSONArray();
        for (int cause = 0; error != null && cause < 3; cause++, error = error.getCause()) {
            JSONObject type = entry(time, "crash.exception").put("build", BuildConfig.VERSION_NAME);
            String name = error.getClass().getName();
            if (SYMBOL.matcher(name).matches()) type.put("symbol", name);
            rows.put(type);
            StackTraceElement[] frames = error.getStackTrace();
            for (int i = 0; i < Math.min(10, frames.length); i++) {
                StackTraceElement frame = frames[i];
                String symbol = frame.getClassName() + "." + frame.getMethodName();
                if (SYMBOL.matcher(symbol).matches())
                    rows.put(entry(time, "crash.frame").put("symbol", symbol)
                            .put("value", Math.max(0, Math.min(1000000, frame.getLineNumber()))));
            }
        }
        AtomicFile file = crashFile(ctx);
        FileOutputStream out = file.startWrite();
        try {
            out.write(rows.toString().getBytes(StandardCharsets.UTF_8));
            file.finishWrite(out);
        } catch (Throwable e) { file.failWrite(out); throw e; }
    }

    static void recentEvents(File[] files, JSONArray rows) throws Exception {
        SimpleDateFormat iso = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        iso.setTimeZone(TimeZone.getTimeZone("UTC"));
        ArrayList<JSONObject> recent = new ArrayList<>();
        // Keep the most recent occurrence of each meaningful event even after discovery
        // emits a burst of generic log messages. The upload still contains at most 20.
        LinkedHashMap<String, Integer> milestones = new LinkedHashMap<>();
        for (int i = Math.max(0, files.length - 2); i < files.length; i++) {
            if (!files[i].getName().matches("piulink-android-[0-9]{8}-[0-9]+\\.jsonl")) continue;
            try (RandomAccessFile file = new RandomAccessFile(files[i], "r")) {
                int size = (int)Math.min(file.length(), 65536);
                long start = file.length() - size;
                file.seek(start);
                byte[] tail = new byte[size];
                file.readFully(tail);
                String[] lines = new String(tail, StandardCharsets.UTF_8).split("\n");
                for (int n = start == 0 ? 0 : 1; n < lines.length; n++) {
                    try {
                        JSONObject local = new JSONObject(lines[n]);
                        String code = local.getString("event");
                        if (!EVENT.matcher(code).matches()) continue;
                        if (!code.startsWith("legacy.") || code.equals("legacy.severe")) {
                            milestones.remove(code);
                            milestones.put(code, recent.size());
                        }
                        recent.add(entry(iso.parse(local.getString("ts")).getTime() / 1000, code));
                    } catch (Exception ignored) { /* A partial line may be visible during rotation. */ }
                }
            } catch (java.io.FileNotFoundException ignored) { }
        }
        TreeSet<Integer> selected = new TreeSet<>(milestones.values());
        while (selected.size() > 14) selected.pollFirst();
        for (int i = recent.size() - 1; i >= 0 && selected.size() < 20; i--) selected.add(i);
        for (int index : selected) rows.put(recent.get(index));
    }

    // Keep the last eight failures separately so routine polling cannot bury a failed attempt.
    public static synchronized void connection(Context ctx, String stage, String reason, int value) {
        try {
            android.content.SharedPreferences prefs = ctx.getSharedPreferences("diagnostic-connections", Context.MODE_PRIVATE);
            JSONArray rows = new JSONArray(prefs.getString("recent", "[]"));
            while (rows.length() >= 8) rows.remove(0);
            rows.put(entry(System.currentTimeMillis()/1000, "connect." + stage).put("reason", reason).put("value", value));
            prefs.edit().putString("recent", rows.toString()).apply();
        } catch (Exception ignored) { }
    }

    static String streamStage(String stage) {
        String name = stage == null ? "" : stage.toLowerCase(Locale.ROOT);
        if (name.contains("rtsp")) return "rtsp";
        if (name.contains("video")) return "video";
        if (name.contains("audio")) return "audio";
        if (name.contains("control")) return "control";
        if (name.contains("input")) return "input";
        if (name.contains("resolution")) return "resolve";
        return "stream";
    }

    public static String upload(Context ctx) throws Exception {
        JSONArray rows = new JSONArray();
        recentEvents(LimeLog.getLogFiles(ctx), rows);
        JSONArray failures = new JSONArray(ctx.getSharedPreferences("diagnostic-connections", Context.MODE_PRIVATE)
                .getString("recent", "[]"));
        for (int i = 0; i < Math.min(8, failures.length()); i++) rows.put(failures.getJSONObject(i));
        AtomicFile file = crashFile(ctx);
        if (file.getBaseFile().exists() && file.getBaseFile().length() <= 24000) {
            JSONArray crash = new JSONArray(new String(file.readFully(), StandardCharsets.UTF_8));
            for (int i = 0; i < Math.min(33, crash.length()); i++) rows.put(crash.getJSONObject(i));
        }
        if (Build.VERSION.SDK_INT >= 30) {
            ActivityManager manager = (ActivityManager)ctx.getSystemService(Context.ACTIVITY_SERVICE);
            boolean readNative = false;
            for (ApplicationExitInfo exit : manager.getHistoricalProcessExitReasons(ctx.getPackageName(), 0, 8)) {
                String code;
                switch (exit.getReason()) {
                    case ApplicationExitInfo.REASON_ANR: code = "exit.anr"; break;
                    case ApplicationExitInfo.REASON_CRASH: code = "exit.crash"; break;
                    case ApplicationExitInfo.REASON_CRASH_NATIVE: code = "exit.native"; break;
                    case ApplicationExitInfo.REASON_LOW_MEMORY: code = "exit.memory"; break;
                    default: continue;
                }
                long time = exit.getTimestamp()/1000;
                rows.put(entry(time, code).put("value", Math.max(0, Math.min(255, exit.getStatus()))));
                if ("exit.native".equals(code) && Build.VERSION.SDK_INT >= 31 && !readNative) {
                    readNative = true;
                    try (java.io.InputStream trace = exit.getTraceInputStream()) {
                        NativeCrashFrames.append(trace, time, rows);
                    } catch (Exception ignored) { /* Some devices retain only the reason. */ }
                }
            }
        }
        if (rows.length() == 0) throw new java.io.IOException("没有可上传的日志");
        JSONObject report = new JSONObject().put("schema", 1).put("platform", "android")
                .put("version", BuildConfig.VERSION_NAME).put("events", rows);
        OkHttpClient http = new OkHttpClient.Builder().callTimeout(18, TimeUnit.SECONDS)
                .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false).build();
        byte[] payload = report.toString().getBytes(StandardCharsets.UTF_8);
        if (payload.length > 16384) throw new java.io.IOException("诊断摘要超出大小限制");
        Request request = new Request.Builder().url(URL).post(RequestBody.create(
                MediaType.get("application/json"), payload)).build();
        try (Response response = http.newCall(request).execute()) {
            if (response.code() == 429) throw new java.io.IOException("提交过于频繁，请稍后再试");
            if (response.code() != 201 || response.body() == null) throw new java.io.IOException("服务器未确认接收");
            String id = response.body().source().readUtf8(20);
            if (!id.matches("[a-f0-9]{20}")) throw new java.io.IOException("报告编号无效");
            return id;
        }
    }
}
