"""Route Moonlight's public LiStart/Interrupt/Stop calls through the per-session stages."""
from pathlib import Path

def integrate(native):
    path = native / "Connection.c"
    text = path.read_text()
    text = text.replace("static int stage = STAGE_NONE;\n", "").replace("static bool alreadyTerminated;\n", "")
    text = text.replace("volatile bool ConnectionInterrupted;\n", "")
    start = text.index("// Interrupt a pending connection attempt.")
    text = text[:start] + Path(__file__).with_name("connection.h").read_text() + "\n" + text[start:]
    text = text.replace("    ConnectionInterrupted = true;", "    pl_connection_interrupt();")
    start = text.index("    // Disable termination callbacks now")
    end = text.index("    if (RemoteAddrString", start)
    text = text[:start] + "    pl_connection_stop(releaseRustStage);\n" + text[end:]
    text = text.replace("if (alreadyTerminated || ConnectionInterrupted)", "if (!pl_connection_claim_termination())")
    text = text.replace("    alreadyTerminated = true;\n", "")
    start = text.index("static bool parseRtspPortNumberFromUrl")
    end = text.index("// Starts the connection", start)
    text = text[:start] + text[end:]
    start = text.index("    // Parse RTSP port number from RTSP session URL")
    end = text.index("    // Validate the audio configuration", start)
    text = text[:start] + """    RtspPortNumber = pl_rtsp_url_port(serverInfo->rtspSessionUrl);
    pl_connection_reset();

""" + text[end:]
    start = text.index('    Limelog("Initializing platform...')
    end = text.index("    // Wiggle the mouse", start)
    text = text[:start] + """    PlConnectionContext ctx = {serverInfo, renderContext, audioContext, drFlags, arFlags};
    err = pl_connection_start(&ctx, runRustStage, notifyRustStage);
    if (err) goto Cleanup;

""" + text[end:]
    path.write_text(text)
    internal = native / "Limelight-internal.h"
    text = internal.read_text()
    old = "extern volatile bool ConnectionInterrupted;"
    if text.count(old) != 1: raise ValueError("Unexpected Moonlight interrupt declaration")
    internal.write_text(text.replace(old, "bool pl_connection_interrupted(void);\n#define ConnectionInterrupted (pl_connection_interrupted())"))
    control = native / "ControlStream.c"
    text = control.read_text()
    if text.count("ConnectionInterrupted = true;") != 6: raise ValueError("Unexpected Moonlight worker failure handling")
    control.write_text(text.replace("ConnectionInterrupted = true;", "LiInterruptConnection();"))
