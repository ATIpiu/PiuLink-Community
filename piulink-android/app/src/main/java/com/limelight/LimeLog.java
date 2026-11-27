package com.limelight;

import android.content.Context;
import android.util.Log;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Unified application diagnostics logger.
 * Levels are intentionally limited to INFO, ERR and DEBUG.
 * Retention is a code-only global policy and is not exposed in settings UI.
 */
public final class LimeLog {
    public enum Level { DEBUG, INFO, ERR }

    public static final Level MINIMUM_LEVEL = Level.INFO;
    public static final int RETENTION_DAYS = 3;
    public static final long MAX_FILE_BYTES = 8L * 1024L * 1024L;
    public static final long MAX_TOTAL_BYTES = 48L * 1024L * 1024L;
    public static final int QUEUE_CAPACITY = 4096;
    private static final long CLEANUP_INTERVAL_MS = 15L * 60L * 1000L;

    private static final String TAG = "PiuLink";
    private static final String RUN_ID = UUID.randomUUID().toString().replace("-", "").substring(0, 12);
    private static final AtomicLong DROPPED = new AtomicLong();
    private static File logDirectory;
    private static volatile boolean initialized;
    private static volatile long nextCleanupMs;
    private static String currentDay;
    private static int currentSegment;
    private static File currentFile;

    private static final ThreadPoolExecutor WRITER = new ThreadPoolExecutor(
            1, 1, 0L, TimeUnit.MILLISECONDS,
            new ArrayBlockingQueue<>(QUEUE_CAPACITY),
            runnable -> {
                Thread t = new Thread(runnable, "PiuLink-Diagnostics");
                t.setDaemon(true);
                return t;
            },
            (runnable, executor) -> {
                DROPPED.incrementAndGet();
                if (!executor.isShutdown()) {
                    executor.getQueue().poll();
                    if (!executor.getQueue().offer(runnable)) DROPPED.incrementAndGet();
                }
            });

    private LimeLog() { }

    public static synchronized void initialize(Context context) {
        if (initialized) return;
        logDirectory = new File(context.getFilesDir(), "logs");
        if (!logDirectory.exists() && !logDirectory.mkdirs()) return;
        initialized = true;
        cleanup();
        infoEvent("diag.start", "diagnostics", "diagnostics logger started", null,
                fields("retention_days", RETENTION_DAYS, "minimum_level", MINIMUM_LEVEL.name()));
    }

    public static File[] getLogFiles(Context context) {
        initialize(context.getApplicationContext());
        File[] files = logDirectory == null ? null : logDirectory.listFiles((dir, name) -> name.endsWith(".jsonl"));
        if (files == null) return new File[0];
        Arrays.sort(files, Comparator.comparingLong(File::lastModified));
        return files;
    }

    /** Legacy compatibility. */
    public static void info(String msg) { infoEvent("legacy.message", "legacy", msg, null, null); }
    /** Legacy warning is mapped to ERR because the public level set intentionally has no WARN. */
    public static void warning(String msg) { errEvent("legacy.warning", "legacy", msg, null, null, null); }
    public static void severe(String msg) { errEvent("legacy.severe", "legacy", msg, null, null, null); }
    public static void debug(String msg) { debugEvent("legacy.debug", "legacy", msg, null, null); }

    public static void infoEvent(String event, String module, String message, String operationId, Map<String, Object> values) {
        write(Level.INFO, event, module, message, operationId, values, null);
    }

    public static void debugEvent(String event, String module, String message, String operationId, Map<String, Object> values) {
        write(Level.DEBUG, event, module, message, operationId, values, null);
    }

    public static void errEvent(String event, String module, String message, Throwable error, String operationId, Map<String, Object> values) {
        write(Level.ERR, event, module, message, operationId, values, error);
    }

    public static Map<String, Object> fields(Object... values) {
        LinkedHashMap<String, Object> map = new LinkedHashMap<>();
        for (int i = 0; i + 1 < values.length; i += 2) map.put(String.valueOf(values[i]), values[i + 1]);
        return map;
    }

    private static void write(Level level, String event, String module, String message, String operationId,
                              Map<String, Object> values, Throwable error) {
        if (!initialized || level.ordinal() < MINIMUM_LEVEL.ordinal()) return;
        StackTraceElement caller = caller();
        final long timestamp = System.currentTimeMillis();
        final int threadId = android.os.Process.myTid();
        final String safeMessage = sanitize(message);
        if (level == Level.ERR) Log.e(TAG, event + " " + safeMessage, error);
        else if (level == Level.DEBUG) Log.d(TAG, event + " " + safeMessage);
        else Log.i(TAG, event + " " + safeMessage);

        WRITER.execute(() -> {
            try {
                if (System.currentTimeMillis() >= nextCleanupMs) cleanup();
                LinkedHashMap<String, Object> root = new LinkedHashMap<>();
                root.put("ts", iso(timestamp));
                root.put("level", level.name());
                root.put("event", event);
                root.put("module", module);
                root.put("message", safeMessage);
                root.put("run_id", RUN_ID);
                root.put("operation_id", operationId);
                root.put("source", caller == null ? null : caller.getFileName());
                root.put("line", caller == null ? null : caller.getLineNumber());
                root.put("member", caller == null ? null : caller.getMethodName());
                root.put("thread", threadId);
                root.put("fields", values);
                long dropped = DROPPED.getAndSet(0);
                root.put("dropped_before", dropped == 0 ? null : dropped);
                if (error != null) {
                    LinkedHashMap<String, Object> ex = new LinkedHashMap<>();
                    ex.put("type", error.getClass().getName());
                    ex.put("message", sanitize(error.getMessage()));
                    ex.put("stack", limit(stack(error), 12000));
                    root.put("exception", ex);
                }
                appendJsonLine(root);
            } catch (Throwable ignored) { }
        });
    }

    private static synchronized void appendJsonLine(Map<String, Object> root) throws IOException {
        File file = currentFile();
        try (FileOutputStream stream = new FileOutputStream(file, true);
             OutputStreamWriter writer = new OutputStreamWriter(stream, StandardCharsets.UTF_8);
             BufferedWriter buffered = new BufferedWriter(writer)) {
            buffered.write(toJson(root));
            buffered.newLine();
        }
    }

    private static synchronized File currentFile() {
        String day = new SimpleDateFormat("yyyyMMdd", Locale.US) {{ setTimeZone(TimeZone.getTimeZone("UTC")); }}.format(new Date());
        if (currentFile == null || !day.equals(currentDay) || currentFile.length() >= MAX_FILE_BYTES) {
            if (!day.equals(currentDay)) { currentDay = day; currentSegment = 0; }
            do { currentFile = new File(logDirectory, String.format(Locale.US, "piulink-android-%s-%02d.jsonl", day, currentSegment++)); }
            while (currentFile.exists() && currentFile.length() >= MAX_FILE_BYTES);
        }
        return currentFile;
    }

    private static synchronized void cleanup() {
        if (logDirectory == null) return;
        nextCleanupMs = System.currentTimeMillis() + CLEANUP_INTERVAL_MS;
        long cutoff = System.currentTimeMillis() - TimeUnit.DAYS.toMillis(RETENTION_DAYS);
        File[] files = logDirectory.listFiles((dir, name) -> name.endsWith(".jsonl"));
        if (files == null) return;
        for (File file : files) if (file.lastModified() < cutoff) file.delete();
        files = logDirectory.listFiles((dir, name) -> name.endsWith(".jsonl"));
        if (files == null) return;
        Arrays.sort(files, Comparator.comparingLong(File::lastModified));
        long total = 0;
        for (File file : files) total += file.length();
        for (File file : files) {
            if (total <= MAX_TOTAL_BYTES) break;
            long size = file.length();
            if (file.delete()) total -= size;
        }
    }

    private static StackTraceElement caller() {
        for (StackTraceElement element : Thread.currentThread().getStackTrace()) {
            String name = element.getClassName();
            if (!name.equals(LimeLog.class.getName()) && !name.equals(Thread.class.getName())) return element;
        }
        return null;
    }

    private static String iso(long millis) {
        SimpleDateFormat format = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US);
        format.setTimeZone(TimeZone.getTimeZone("UTC"));
        return format.format(new Date(millis));
    }

    private static String sanitize(String value) { return value == null ? "" : value.replace("\r", "\\r").replace("\n", "\\n"); }
    private static String limit(String value, int max) { return value.length() <= max ? value : value.substring(0, max) + "…"; }
    private static String stack(Throwable error) {
        StringWriter out = new StringWriter();
        error.printStackTrace(new PrintWriter(out));
        return sanitize(out.toString());
    }

    @SuppressWarnings("unchecked")
    private static String toJson(Object value) {
        if (value == null) return "null";
        if (value instanceof Number || value instanceof Boolean) return String.valueOf(value);
        if (value instanceof Map) {
            StringBuilder b = new StringBuilder("{"); boolean first = true;
            for (Map.Entry<Object, Object> e : ((Map<Object, Object>) value).entrySet()) {
                if (!first) b.append(','); first = false;
                b.append(quote(String.valueOf(e.getKey()))).append(':').append(toJson(e.getValue()));
            }
            return b.append('}').toString();
        }
        if (value instanceof Iterable) {
            StringBuilder b = new StringBuilder("["); boolean first = true;
            for (Object item : (Iterable<?>) value) { if (!first) b.append(','); first = false; b.append(toJson(item)); }
            return b.append(']').toString();
        }
        return quote(String.valueOf(value));
    }

    private static String quote(String value) {
        StringBuilder b = new StringBuilder("\"");
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            switch (c) {
                case '\\': b.append("\\\\"); break;
                case '"': b.append("\\\""); break;
                case '\b': b.append("\\b"); break;
                case '\f': b.append("\\f"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20) b.append(String.format(Locale.US, "\\u%04x", (int)c)); else b.append(c);
            }
        }
        return b.append('"').toString();
    }

    /** Kept for source compatibility with older diagnostics code. */
    public static void setFileHandler(String ignored) throws IOException { throw new IOException("External log handlers are no longer supported"); }
}
