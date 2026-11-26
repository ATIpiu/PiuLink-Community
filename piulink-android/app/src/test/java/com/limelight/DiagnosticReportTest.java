package com.limelight;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;

import static org.junit.Assert.*;

public class DiagnosticReportTest {
    @Rule public TemporaryFolder logs = new TemporaryFolder();

    private static String line(String code, int second) throws Exception {
        return new JSONObject()
                .put("ts", String.format(java.util.Locale.ROOT, "2026-11-26T03:07:%02d.000Z", second))
                .put("event", code).put("message", "private detail").toString();
    }

    @Test public void pollingBurstAfterRestartPreservesLastStallAndStart() throws Exception {
        File previous = logs.newFile("piulink-android-20261126-00.jsonl");
        Files.write(previous.toPath(), java.util.Arrays.asList(line("stream.android.video_stalled", 0),
                line("diag.start", 1)), StandardCharsets.UTF_8);
        File current = logs.newFile("piulink-android-20261126-01.jsonl");
        List<String> burst = new ArrayList<>();
        for (int i = 2; i <= 42; i++) burst.add(line("legacy.message", i));
        burst.add(line("transfer.android.pair_refresh_failed", 43));
        burst.add(line("private.invalid", 44));
        burst.add("{");
        Files.write(current.toPath(), burst, StandardCharsets.UTF_8);

        JSONArray rows = new JSONArray();
        DiagnosticReport.recentEvents(new File[]{previous, current}, rows);
        assertEquals(20, rows.length());
        assertEquals("stream.android.video_stalled", rows.getJSONObject(0).getString("code"));
        assertEquals("diag.start", rows.getJSONObject(1).getString("code"));
        assertEquals("transfer.android.pair_refresh_failed", rows.getJSONObject(19).getString("code"));
        long prior = 0;
        for (int i = 0; i < rows.length(); i++) {
            JSONObject event = rows.getJSONObject(i);
            assertTrue(event.getLong("time") > prior);
            prior = event.getLong("time");
            assertEquals(2, event.length());
        }
        assertFalse(rows.toString().contains("private"));
    }
}
