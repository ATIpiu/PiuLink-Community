from pathlib import Path


def integrate(native):
    control = native / "ControlStream.c"
    code = control.read_text()
    start = code.index("        // Create a client",code.index("int startControlStream(void)"))
    end = code.index("\n    }\n    else {",start)
    code = code[:start] + """        PlConnectAddresses addresses = { &remoteAddress, &localAddress };
        err = pl_enet_connect(&addresses, CONTROL_STREAM_TIMEOUT_SEC * 1000, connectStep);
        if (err) { stopping = true; return err; }
""" + code[end:]
    code = code.replace("// Starts the control stream",Path(__file__).with_suffix(".inc").read_text() + "\n// Starts the control stream")
    start = code.index("            if (ENET_TIME_LESS(peer->nextTimeout")
    end = code.index("\n        }\n", start)
    code = code[:start] + """            waitTimeMs = pl_enet_wait(peer->nextTimeout, client->serviceTime, peer->pingInterval);""" + code[end:]
    control.write_text('#include <stdint.h>\nextern uint32_t pl_enet_wait(uint32_t,uint32_t,uint32_t);\n' + code)
    misc = native / "Misc.c"
    code = misc.read_text()
    start = code.index("static int serviceEnetHostInternal(")
    end = code.index("\nint serviceEnetHost(", start)
    misc.write_text(code[:start] + """typedef struct { ENetHost *client; ENetEvent *event; bool ignore; } PlService;
extern int32_t pl_enet_service(void *,uint32_t,uint32_t,int32_t (*)(void *,int32_t,uint32_t));
static int32_t serviceSlice(void *opaque,int32_t unused,uint32_t timeout) {
    PlService *s = opaque;
    if (!s->ignore && ConnectionInterrupted) { SetLastSocketError(EINTR); return -1; }
    return enet_host_service(s->client,s->event,timeout);
}
static int serviceEnetHostInternal(ENetHost *client, ENetEvent *event, enet_uint32 timeout, bool ignore) {
    PlService s = {client,event,ignore};
    SetLastSocketError(0);
    return pl_enet_service(&s,timeout,ENET_INTERNAL_TIMEOUT_MS,serviceSlice);
}
""" + code[end:])
