typedef struct { ENetAddress *remote, *local; } PlConnectAddresses;
extern int32_t pl_enet_connect(void *,uint32_t,int32_t (*)(void *,int32_t,uint32_t));
extern uint32_t pl_enet_wait(uint32_t,uint32_t,uint32_t);

static int32_t connectStep(void *ctx,int32_t op,uint32_t timeout) {
    PlConnectAddresses *addresses=ctx;
    switch (op) {
    case 0:
        client=enet_host_create(RemoteAddr.ss_family, LocalAddr.ss_family ? addresses->local : NULL,
                                1,CTRL_CHANNEL_COUNT,0,0);
        if (!client) return 0;
        bindSocketToAndroidNetwork(client->socket);
        client->intercept=ignoreDisconnectIntercept;
        enet_socket_set_option(client->socket,ENET_SOCKOPT_QOS,1);
        return 1;
    case 1:
        peer=enet_host_connect(client,addresses->remote,CTRL_CHANNEL_COUNT,ControlConnectData);
        return peer != NULL;
    case 2: {
        ENetEvent event;
        int err=serviceEnetHost(client,&event,timeout);
        if (err <= 0) return err == 0 ? ETIMEDOUT : LastSocketFail();
        if (event.type == ENET_EVENT_TYPE_CONNECT) return 0;
        int failure = LastSocketError() ? LastSocketFail() : (event.type ? (int)event.type : -1);
        if (event.type == ENET_EVENT_TYPE_RECEIVE) enet_packet_destroy(event.packet);
        return failure;
    }
    case 3: enet_host_flush(client); enet_peer_timeout(peer,2,10000,10000); return 0;
    case 4: enet_peer_reset(peer); peer=NULL; return 0;
    case 5: enet_host_destroy(client); client=NULL; return 0;
    default: return -1;
    }
}
