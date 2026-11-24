package com.limelight.preferences;

import org.junit.Test;
import org.junit.Rule;
import org.junit.rules.TemporaryFolder;
import java.io.File;
import java.nio.file.Files;
import java.nio.charset.StandardCharsets;
import static org.junit.Assert.*;

public class LogPagesTest {
    @Rule public TemporaryFolder temp = new TemporaryFolder();

    @Test public void preservesUtf8AndChronologicalOrderAcrossPagesAndFiles() throws Exception {
        File first = temp.newFile("a.log"), second = temp.newFile("b.log");
        String prefix = new String(new char[LogPages.PAGE_BYTES - 1]).replace('\0', 'a');
        String text = prefix + "平板🙂";
        Files.write(first.toPath(), text.getBytes(StandardCharsets.UTF_8));
        Files.write(second.toPath(), "new".getBytes(StandardCharsets.UTF_8));
        assertTrue(first.setLastModified(second.lastModified() - 2000));
        LogPages p = new LogPages(new File[]{second, first});
        assertEquals("new", p.read()); assertFalse(p.hasNewer());
        p.move(-1); String tail = p.read();
        p.move(-1); assertFalse(p.hasOlder()); assertEquals(text, p.read() + tail);
        p.move(1); assertEquals(tail, p.read());
        p.move(1); assertEquals("new", p.read());
    }

    @Test public void handlesEmptyAndRotatedFiles() throws Exception {
        File f = temp.newFile();
        LogPages p = new LogPages(new File[]{f});
        assertEquals("", p.read()); assertFalse(p.hasOlder()); assertFalse(p.hasNewer());
        Files.write(f.toPath(), new byte[LogPages.PAGE_BYTES * 2]);
        p = new LogPages(new File[]{f});
        assertEquals(LogPages.PAGE_BYTES, p.read().length());
        Files.write(f.toPath(), new byte[0]);
        assertEquals("", p.read());
    }
}
