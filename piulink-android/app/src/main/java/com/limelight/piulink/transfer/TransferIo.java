package com.limelight.piulink.transfer;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Locale;

/** Protocol-v1 limits and streaming helpers; intentionally independent of Android APIs. */
public final class TransferIo {
    public static final long MAX_BYTES = 2_147_483_648L;
    private TransferIo() { }

    public static void validateSize(long size) throws IOException {
        if (size < 0 || size > MAX_BYTES) throw new IOException("文件大小无效，单文件上限为 2 GiB");
    }

    public static String safeName(String value) {
        if (value == null || value.trim().isEmpty()) return "shared-file";
        String name = value == null ? "" : value.replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "_").trim();
        name = name.replaceAll("[. ]+$", "");
        if (name.isEmpty()) name = "shared-file";
        String stem = name.split("\\.", 2)[0].trim().toUpperCase(Locale.ROOT);
        if (stem.matches("CON|PRN|AUX|NUL|(?:COM|LPT)[1-9¹²³]")) name = "_" + name;
        // Use a byte limit, not a UTF-16 character limit. 180 Chinese characters exceed 512 bytes.
        int end = 0, bytes = 0;
        while (end < name.length() && end < 180) {
            int codepoint = name.codePointAt(end);
            int chars = Character.charCount(codepoint);
            int count = new String(Character.toChars(codepoint)).getBytes(StandardCharsets.UTF_8).length;
            if (bytes + count > 240 || end + chars > 180) break;
            bytes += count; end += chars;
        }
        name = name.substring(0, end).replaceAll("[. ]+$", "");
        return name.isEmpty() ? "shared-file" : name;
    }

    public static void copyExactly(InputStream input, OutputStream output, long size, java.util.function.LongConsumer progress) throws IOException {
        validateSize(size);
        byte[] buffer = new byte[65536];
        long remaining = size;
        while (remaining > 0) {
            interrupted();
            int count = input.read(buffer, 0, (int)Math.min(buffer.length, remaining));
            if (count < 0) throw new EOFException("源文件提前结束");
            if (count == 0) {
                int one = input.read();
                if (one < 0) throw new EOFException("源文件提前结束");
                output.write(one); remaining--;
            } else { output.write(buffer, 0, count); remaining -= count; }
            if (progress != null) progress.accept(size - remaining);
        }
    }

    /** A provider is allowed to omit OpenableColumns.SIZE. Stage it without retaining it in RAM. */
    public static File stageUnknown(InputStream input, File cacheDirectory) throws IOException {
        File result = File.createTempFile("piulink-upload-", ".tmp", cacheDirectory);
        boolean complete = false;
        try {
            try (OutputStream output = new BufferedOutputStream(new FileOutputStream(result))) {
                byte[] buffer = new byte[65536];
                long total = 0;
                while (true) {
                    interrupted();
                    int count = input.read(buffer);
                    if (count < 0) break;
                    if (count == 0) {
                        int one = input.read(); if (one < 0) break;
                        if (++total > MAX_BYTES) throw new IOException("文件不能超过 2 GiB");
                        output.write(one);
                    } else {
                        if (total > MAX_BYTES - count) throw new IOException("文件不能超过 2 GiB");
                        output.write(buffer, 0, count); total += count;
                    }
                }
            }
            complete = true;
            return result;
        } finally { if (!complete) result.delete(); }
    }

    private static void interrupted() throws InterruptedIOException {
        if (Thread.currentThread().isInterrupted()) throw new InterruptedIOException("传输已取消");
    }
}
