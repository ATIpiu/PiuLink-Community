from pathlib import Path
import shutil

def integrate(native):
    here = Path(__file__).parent
    shutil.copy2(here / "media.h", native / "piulink_media.h")
    for name in ["Video", "Audio"]:
        path = native / (name + "Stream.c")
        code = '#include "piulink_media.h"\n' + path.read_text()
        start = code.index("static void " + name + "PingThreadProc(")
        end = code.index("\n}",start) + 2
        code = code[:start] + f"""static bool pingInterrupted(void) {{ return PltIsThreadInterrupted(&udpPingThread); }}
static void sleepForNextPing(void) {{ PltSleepMsInterruptible(&udpPingThread, 500); }}
static void {name}PingThreadProc(void* context) {{
    LC_SOCKADDR peer;
    memcpy(&peer, &RemoteAddr, sizeof(peer));
    SET_PORT(&peer, {name}PortNumber);
    pl_media_ping(rtpSocket, &peer, AddrLen, (const uint8_t*){name}PingPayload.payload, pingInterrupted, sleepForNextPing);
}}""" + code[end:]
        start = code.index(f"static void {name}ReceiveThreadProc(void* context)")
        end = code.index("\n}",start) + 2
        code = code[:start] + (here / (name.lower() + "_receive.inc")).read_text() + code[end:]
        video = "true" if name == "Video" else "false"
        code = code.replace("static bool receivedDataFromPeer;\n","").replace("    receivedDataFromPeer = false;", f"    pl_media_reset({video});")
        code = code.replace("if (!receivedDataFromPeer)", f"if (!pl_media_seen({video}))")
        for line in ["static uint64_t firstDataTimeMs;", "static bool receivedFullFrame;", "static uint64_t firstReceiveTime;",
                     "    firstReceiveTime = 0;", "    firstDataTimeMs = 0;", "    receivedFullFrame = false;"]:
            code = code.replace(line+"\n","")
        code = code.replace("    receivedFullFrame = true;", "    pl_media_keyframe();")
        if name=="Audio":
            # The ping owner closes the shared socket in destroyAudioStream, also on start failure.
            start = code.index("int startAudioStream(")
            code = code[:start] + code[start:].replace("        closeSocket(rtpSocket);\n","")
        path.write_text(code)
