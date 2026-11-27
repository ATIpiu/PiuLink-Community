from pathlib import Path


def integrate(core):
    source = core / "callbacks.c"
    code = source.read_text()
    begin = code.index("    // Use a 32K frame buffer")
    end = code.index("    return 0;", begin)
    code = code[:begin] + "    DecodedFrameBuffer = NULL;  // Allocate on the first decode unit.\n" + code[end:]
    code = code.replace("    (*env)->DeleteGlobalRef(env, DecodedFrameBuffer);",
                        "    if (DecodedFrameBuffer) (*env)->DeleteGlobalRef(env, DecodedFrameBuffer);\n"
                        "    DecodedFrameBuffer = NULL;", 1)
    begin = code.index("int BridgeDrSubmitDecodeUnit(")
    end = code.index("int BridgeArInit(", begin)
    source.write_text(code[:begin] + Path(__file__).with_suffix(".inc").read_text() + "\n" + code[end:])
