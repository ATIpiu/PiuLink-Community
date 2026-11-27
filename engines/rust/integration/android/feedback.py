from pathlib import Path


def integrate(native):
    path = native / "ControlStream.c"
    code = path.read_text()
    start = code.index("static uint32_t lastGoodFrame;")
    end = code.index("static bool stopping;", start)
    code = code[:start] + Path(__file__).with_suffix(".h").read_text() + "\n" + code[end:]
    start = code.index("static int intervalGoodFrameCount;")
    end = code.index("static uint32_t currentEnetSequenceNumber;", start)
    code = code[:start] + code[end:]
    code = code.replace("static uint64_t firstFrameTimeMs;", "")
    start = code.index("    lastGoodFrame = 0;")
    end = code.index("    currentEnetSequenceNumber = 0;", start)
    code = code[:start] + "    pl_health_reset();\n    disconnectPending = false;\n" + code[end:]
    code = code.replace("    lastGoodFrame = frameIndex;\n    intervalGoodFrameCount++;", "    pl_health_complete(frameIndex);")
    start = code.index("    LC_ASSERT_VT", code.index("void connectionSawFrame("))
    end = code.index("\n}", start)
    code = code[:start] + """    int status = pl_health_seen(frameIndex, PltGetMillis());
    if (status >= 0) ListenerCallbacks.connectionStatusUpdate(status);""" + code[end:]
    start = code.index("static void lossStatsThreadFunc(")
    end = code.index("static void invalidateRefFramesFunc(",start)
    code = code[:start]+Path(__file__).with_suffix(".inc").read_text()+"\n"+code[end:]
    code = code.replace("lastSeenFrame != 0", "pl_latest_frame() != 0")
    path.write_text(code)
