package com.limelight;

import com.google.protobuf.CodedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import org.json.JSONArray;
import org.json.JSONObject;

/** Reads only relative instruction offsets for known libraries in the faulting thread.
 * Field numbers are from AOSP debuggerd/proto/tombstone.proto. */
final class NativeCrashFrames {
    private static final int LIMIT = 2 * 1024 * 1024;
    private NativeCrashFrames() {}

    static void append(InputStream input, long time, JSONArray events) throws Exception {
        if (input == null) return;
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        for (int n; (n = input.read(buf)) != -1;) {
            if (out.size() + n > LIMIT) return;
            out.write(buf, 0, n);
        }
        byte[] data = out.toByteArray();
        CodedInputStream root = CodedInputStream.newInstance(data);
        int tid = 0;
        for (int tag; (tag = root.readTag()) != 0;) {
            if (tag == (6 << 3)) { tid = root.readUInt32(); break; }
            if (!root.skipField(tag)) return;
        }
        if (tid == 0) return;
        root = CodedInputStream.newInstance(data);
        for (int tag; (tag = root.readTag()) != 0;) {
            if (tag != ((16 << 3) | 2)) { if (!root.skipField(tag)) return; continue; }
            CodedInputStream map = CodedInputStream.newInstance(root.readByteArray());
            int key = 0;
            byte[] thread = null;
            for (int field; (field = map.readTag()) != 0;) {
                if (field == 8) key = map.readUInt32();
                else if (field == 18) thread = map.readByteArray();
                else if (!map.skipField(field)) throw new IOException("Malformed thread");
            }
            if (key == tid && thread != null) {
                CodedInputStream t = CodedInputStream.newInstance(thread);
                int remaining = 16;
                for (int field; remaining > 0 && (field = t.readTag()) != 0;) {
                    if (field == 34) {
                        if (appendFrame(t.readByteArray(), time, events)) remaining--;
                    } else if (!t.skipField(field)) return;
                }
                return;
            }
        }
    }

    private static boolean appendFrame(byte[] data, long time, JSONArray events) throws Exception {
        CodedInputStream frame = CodedInputStream.newInstance(data);
        long pc = 0;
        String library = "";
        for (int tag; (tag = frame.readTag()) != 0;) {
            if (tag == 8) pc = frame.readUInt64();
            else if (tag == 50) {
                String path = frame.readStringRequireUtf8();
                library = path.substring(path.lastIndexOf('/') + 1);
            } else if (!frame.skipField(tag)) return false;
        }
        if (pc < 0 || pc > Integer.MAX_VALUE) return false;
        switch (library) {
            case "libmoonlight-core.so": case "libgojni.so": case "libc.so":
            case "libart.so": case "libmediandk.so": case "libstagefright.so": break;
            default: return false;
        }
        events.put(new JSONObject().put("time", time).put("code", "crash.native_frame")
                .put("symbol", library.replace('-', '_')).put("value", pc));
        return true;
    }
}
