package com.limelight;

import com.google.protobuf.CodedOutputStream;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import org.json.JSONArray;
import org.junit.Test;
import static org.junit.Assert.*;

public class NativeCrashFramesTest {
    interface Fields { void write(CodedOutputStream wire) throws Exception; }
    private byte[] message(Fields fields) throws Exception {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        CodedOutputStream wire = CodedOutputStream.newInstance(bytes);
        fields.write(wire);
        wire.flush();
        return bytes.toByteArray();
    }
    @Test public void extractsOnlyRelativeOffsetAndKnownLibraryFromFaultingThread() throws Exception {
        byte[] frame = message(w -> {
            w.writeUInt64(1, 1234567);
            w.writeUInt64(2, 87654321);
            w.writeString(4, "private-function-data");
            w.writeString(6, "/data/app/private-directory/libmoonlight-core.so");
        });
        byte[] foreign = message(w -> w.writeString(6, "/private/user-selected-file"));
        byte[] thread = message(w -> {
            w.writeByteArray(4, frame);
            w.writeByteArray(4, foreign);
            w.writeByteArray(5, new byte[]{7, 8});
        });
        byte[] tombstone = message(w -> {
            w.writeString(14, "private-abort-message");
            w.writeByteArray(16, message(m -> { m.writeUInt32(1, 42); m.writeByteArray(2, thread); }));
            w.writeByteArray(16, message(m -> { m.writeUInt32(1, 43); m.writeByteArray(2, thread); }));
            w.writeUInt32(6, 43);
        });
        JSONArray rows = new JSONArray();
        NativeCrashFrames.append(new ByteArrayInputStream(tombstone), 1770000000, rows);
        assertEquals(1, rows.length());
        assertEquals("libmoonlight_core.so", rows.getJSONObject(0).getString("symbol"));
        assertEquals(1234567, rows.getJSONObject(0).getInt("value"));
        assertFalse(rows.toString().contains("private"));
        assertFalse(rows.toString().contains("87654321"));
    }
}
