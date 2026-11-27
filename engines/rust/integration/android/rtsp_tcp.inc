typedef struct {
    int (*open)(bool);
    bool (*interrupted)(void);
    uint8_t *(*serialize)(void *,int *);
    void (*release)(void *);
    void (*preconnect)(void *);
    bool (*finish)(void *,int,uint8_t *,int);
} PlRtspTcp;
extern bool pl_rtsp_tcp(void *,bool,const PlRtspTcp *,int *);
typedef struct {PRTSP_MESSAGE request,response;} TcpContext;

static int openRtspSocket(bool pending) {
    int fd;
    if (pending) {
        fd=finishTcpConnect(nextHandshakeSocket,RTSP_CONNECT_TIMEOUT_SEC);
        nextHandshakeSocket=INVALID_SOCKET;
    } else fd=connectTcpSocket(&RemoteAddr,AddrLen,RtspPortNumber,RTSP_CONNECT_TIMEOUT_SEC);
    if (fd != INVALID_SOCKET) return fd;
    int error=LastSocketError();
    return error ? -error : -EIO;
}
static bool rtspInterrupted(void) {return ConnectionInterrupted;}
static uint8_t *sealRequest(void *ctx,int *size) {
    return (uint8_t*)sealRtspMessage(((TcpContext*)ctx)->request,size);
}
static void preconnectRtsp(void *ctx) {
    if (IS_SUNSHINE() && !ConnectionInterrupted && strcmp(((TcpContext*)ctx)->request->message.request.command,"PLAY")!=0)
        nextHandshakeSocket=beginTcpConnect(&RemoteAddr,AddrLen,RtspPortNumber);
}
static bool finishRtsp(void *ctx,int fd,uint8_t *bytes,int size) {
    if (!LocalAddr.ss_family) {
        SOCKADDR_LEN len=sizeof(LocalAddr);
        if (getsockname(fd,(struct sockaddr*)&LocalAddr,&len)<0) memset(&LocalAddr,0,sizeof(LocalAddr));
    }
    return unsealRtspMessage((char*)bytes,size,((TcpContext*)ctx)->response);
}
static bool transactRtspMessageTcp(PRTSP_MESSAGE request,PRTSP_MESSAGE response,int *error) {
    static const PlRtspTcp ops={openRtspSocket,rtspInterrupted,sealRequest,free,preconnectRtsp,finishRtsp};
    TcpContext ctx={request,response};
    return pl_rtsp_tcp(&ctx,nextHandshakeSocket!=INVALID_SOCKET,&ops,error);
}
