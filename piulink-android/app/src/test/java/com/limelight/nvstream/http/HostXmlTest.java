package com.limelight.nvstream.http;

import java.io.IOException;
import okhttp3.ResponseBody;
import okio.Buffer;
import org.junit.Test;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertThrows;

public class HostXmlTest {
    @Test
    public void acceptsExactLimitWithUnknownLength() throws IOException {
        try (ResponseBody body = ResponseBody.create(null, -1, new Buffer().writeUtf8("1234"))) {
            assertEquals("1234", NvHTTP.readHostXml(body, 4));
        }
    }

    @Test
    public void rejectsOversizeDeclaredAndStreamedBodies() {
        try (ResponseBody declared = ResponseBody.create(null, 5, new Buffer());
             ResponseBody streamed = ResponseBody.create(null, -1, new Buffer().writeUtf8("12345"))) {
            assertThrows(IOException.class, () -> NvHTTP.readHostXml(declared, 4));
            assertThrows(IOException.class, () -> NvHTTP.readHostXml(streamed, 4));
        }
    }
}
