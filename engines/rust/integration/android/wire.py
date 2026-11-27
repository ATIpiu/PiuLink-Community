"""Compile the shared Rust wire codec for every Android ABI and link it into Moonlight."""
import os
from pathlib import Path
import re
import shutil
import subprocess

TARGETS = {
    "arm64-v8a": "aarch64-linux-android",
    "armeabi-v7a": "armv7-linux-androideabi",
    "x86": "i686-linux-android",
    "x86_64": "x86_64-linux-android",
}


def build_archives(project):
    wire = Path(__file__).resolve().parents[1] / "wire"
    core = project / "app/src/main/jni/moonlight-core"
    native = core / "moonlight-common-c/src"
    suffix = ".exe" if os.name == "nt" else ""
    cargo = shutil.which("cargo") or Path.home() / ".cargo/bin" / ("cargo" + suffix)
    target_dir = (project / ".rust-build").resolve()
    for abi, target in TARGETS.items():
        output = core / "rust-wire" / abi
        output.mkdir(parents=True, exist_ok=True)
        subprocess.run([cargo, "build", "--locked", "--release", "--target", target,
                        "--manifest-path", wire / "Cargo.toml", "--target-dir", target_dir], check=True)
        shutil.copy2(target_dir / target / "release/libpiulink_wire.a", output)


def integrate(project):
    build_archives(project)
    wire = Path(__file__).resolve().parents[1] / "wire"
    core = project / "app/src/main/jni/moonlight-core"
    native = core / "moonlight-common-c/src"
    shutil.copy2(wire / "fec.h", native / "piulink_fec.h")
    queue = native / "RtpVideoQueue.c"
    code = queue.read_text()
    anchor = "uint32_t fecIndex = (nvPacket->fecInfo & 0x3FF000) >> 12;"
    if code.count(anchor) != 1:
        raise RuntimeError("Unexpected Moonlight video queue")
    code = code.replace(anchor, "PlVideoFec fec = pl_fec_read(nvPacket->fecInfo, nvPacket->multiFecBlocks);\n    uint32_t fecIndex = fec.index;")
    for expr, field in [("(nvPacket->fecInfo & 0xFFC00000) >> 22", "data"),
                        ("(nvPacket->fecInfo & 0xFF0) >> 4", "percentage"),
                        ("(nvPacket->multiFecBlocks >> 4) & 0x3", "current"),
                        ("(nvPacket->multiFecBlocks >> 6) & 0x3", "last")]:
        if expr not in code:
            raise RuntimeError("Incompatible Moonlight FEC field " + field)
        code = code.replace(expr, "fec." + field)
    from block import integrate_block
    code = integrate_block(native, wire, code)
    from frame import integrate_frame, integrate_audio
    from annexb import integrate_nal
    integrate_audio(native)
    integrate_nal(native)
    code = integrate_frame(native, code)
    import sys
    sys.path.insert(0, str(wire.parent / "portable"))
    from client import readiness
    shutil.copy2(wire.parent / "portable/piulink_portable.h", native)
    queue.write_text('#include "piulink_fec.h"\n#include "piulink_portable.h"\n' + readiness(code))
    shutil.copy2(wire / "rtsp_adapter.h", native / "piulink_rtsp.h")
    parser = native / "RtspParser.c"
    original = parser.read_text()
    a = original.index("// Check if String s begins")
    b = original.index("// Create new RTSP message struct", a)
    original = original[:a] + """#include "piulink_rtsp.h"
int parseRtspMessage(PRTSP_MESSAGE msg, char *data, int len) {
    return pl_rtsp_parse(msg, data, len, malloc, free);
}
""" + original[b:]
    a = original.index("bool appendString(")
    b = original.index("// Free everything", a)
    parser.write_text(original[:a] + """char *serializeRtspMessage(PRTSP_MESSAGE msg, int *len) {
    return pl_rtsp_serialize(msg, len, malloc);
}
""" + original[b:])
    from session import client as integrate_session
    integrate_session(native)
    from connection import integrate as integrate_connection
    integrate_connection(native)
    from opus import integrate as integrate_opus
    integrate_opus(core, wire)
    from decode_unit import integrate as integrate_decode_unit
    integrate_decode_unit(core)
    from presentation import integrate as integrate_presentation
    integrate_presentation(project, core)
    from decoder import integrate as integrate_decoder
    integrate_decoder(project, core)
    from enet import integrate as integrate_enet
    integrate_enet(native)
    from client_pair import integrate as integrate_pair
    integrate_pair(project, core)
    from media import integrate as integrate_media
    integrate_media(native)
    from feedback import integrate as integrate_feedback
    integrate_feedback(native)
    makefile = core / "Android.mk"
    source = native / "InputStream.c"
    text = source.read_text()
    start = text.index("    // For proper behavior", text.index("int LiSendKeyboardEvent2"))
    end = text.index("    err = LbqOfferQueueItem", start)
    source.write_text('#include "piulink_fec.h"\n' + text[:start] + """    _Static_assert(sizeof(NV_KEYBOARD_PACKET) == 14, "Unexpected keyboard wire layout");
    pl_write_key((uint8_t*)&holder->packet.keyboard, keyCode, keyAction, modifiers, flags, IS_SUNSHINE());

""" + text[end:])
    import sys
    sys.path.insert(0, str(wire))
    from record_adapter import client as integrate_rtsp_record
    integrate_rtsp_record(native, wire)
    from media_crypto_adapter import client as integrate_media_crypto
    integrate_media_crypto(native)
    from hardening import android as harden_android
    harden_android(project, native)
    from pointer_adapter import client
    from input_adapter import client_inputs
    source.write_text(client_inputs(client(source.read_text())))
    code = makefile.read_text()
    lines = code.splitlines(keepends=True)
    if sum("moonlight-common-c/src/ByteBuffer.c" in line for line in lines) != 1:
        raise RuntimeError("Unexpected Moonlight control buffer build")
    code = "".join(line for line in lines if "moonlight-common-c/src/ByteBuffer.c" not in line)
    anchor = "LOCAL_PATH := $(MY_LOCAL_PATH)"
    if code.count(anchor) != 1 or code.count("LOCAL_STATIC_LIBRARIES := libopus") != 1:
        raise RuntimeError("Unexpected Moonlight native build")
    makefile.write_text(code.replace(anchor, anchor + """
include $(CLEAR_VARS)
LOCAL_MODULE := piulink_wire
LOCAL_SRC_FILES := rust-wire/$(TARGET_ARCH_ABI)/libpiulink_wire.a
include $(PREBUILT_STATIC_LIBRARY)
""").replace("LOCAL_STATIC_LIBRARIES := libopus", "LOCAL_STATIC_LIBRARIES := piulink_wire libopus"))
