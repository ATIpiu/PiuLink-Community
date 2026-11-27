package com.limelight.preferences;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Comparator;

/** A snapshot of log lengths. Appends become visible on refresh; each read has a fixed upper bound. */
final class LogPages {
    static final int PAGE_BYTES = 16 * 1024;
    final File[] files;
    private final long[] sizes;
    int fileIndex;
    long pageIndex;

    LogPages(File[] inventory) {
        files = Arrays.stream(inventory).filter(f -> f.isFile() && f.length() > 0)
                .sorted(Comparator.comparingLong(File::lastModified).thenComparing(File::getName)).toArray(File[]::new);
        sizes = new long[files.length];
        for (int i = 0; i < files.length; i++) sizes[i] = files[i].length();
        fileIndex = files.length - 1;
        if (fileIndex >= 0) pageIndex = lastPage();
    }

    private long lastPage() { return Math.max(0, sizes[fileIndex] - 1) / PAGE_BYTES; }
    boolean hasOlder() { return fileIndex > 0 || pageIndex > 0; }
    boolean hasNewer() { return fileIndex >= 0 && (fileIndex < files.length - 1 || pageIndex < lastPage()); }

    void move(int direction) {
        if (direction < 0 && hasOlder()) {
            if (pageIndex > 0) pageIndex--;
            else { fileIndex--; pageIndex = lastPage(); }
        } else if (direction > 0 && hasNewer()) {
            if (pageIndex < lastPage()) pageIndex++;
            else { fileIndex++; pageIndex = 0; }
        }
    }

    String read() throws IOException {
        if (fileIndex < 0) return "";
        try (RandomAccessFile input = new RandomAccessFile(files[fileIndex], "r")) {
            long limit = Math.min(input.length(), sizes[fileIndex]);
            long start = aligned(input, Math.min(pageIndex * PAGE_BYTES, limit), limit);
            long end = aligned(input, Math.min((pageIndex + 1) * PAGE_BYTES, limit), limit);
            byte[] bytes = new byte[(int)(end - start)];
            input.seek(start);
            input.readFully(bytes);
            return new String(bytes, StandardCharsets.UTF_8);
        }
    }

    private static long aligned(RandomAccessFile input, long offset, long limit) throws IOException {
        input.seek(offset);
        // A UTF-8 character has at most three continuation bytes.
        for (int i = 0; i < 3 && offset < limit && (input.read() & 0xc0) == 0x80; i++) offset++;
        return offset;
    }
}
