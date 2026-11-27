from pathlib import Path

def client(native):
    path = native / "RtspConnection.c"
    code = path.read_text()
    start = code.index("static int parseOpusConfigFromParamString")
    end = code.index("static bool parseUrlAddrFromRtspUrlString", start)
    code = code[:start] + code[end:]
    start = code.index("int performRtspHandshake(")
    code = code[:start] + Path(__file__).with_name("session.h").read_text() + "\n" + code[start:]
    start = code.index("    {\n        RTSP_MESSAGE response;", code.index("int performRtspHandshake("))
    end = code.index("Exit:", start)
    code = code[:start] + """    ret = pl_rtsp_handshake(serverInfo, AppVersionQuad[0], APP_VERSION_AT_LEAST(7, 1, 431), exchangeRustRtsp);
""" + code[end:]
    # Preserve common cleanup on ENet allocation, connection and timeout failures.
    start = code.index("    // Setup ENet if required")
    end = code.index("    ret = pl_rtsp_handshake", start)
    code = code[:start] + code[start:end].replace("            return -1;", "            ret = -1;\n            goto Exit;") + code[end:]
    path.write_text(code)
