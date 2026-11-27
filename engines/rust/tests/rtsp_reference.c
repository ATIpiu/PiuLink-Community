#include "Rtsp.h"
#include "rtsp_adapter.h"
#include <assert.h>

static int fail_on, calls, live;
static void *tracked_alloc(size_t bytes) {
    if (++calls == fail_on) return NULL;
    void *p = malloc(bytes);
    if (p) ++live;
    return p;
}
static void tracked_free(void *p) { if (p) --live; free(p); }
static void compare(char *wire, int length) {
    RTSP_MESSAGE c = {0}, rust = {0};
    assert(parseRtspMessage(&c, wire, length) == 0);
    assert(pl_rtsp_parse(&rust, wire, length, malloc, free) == 0);
    assert(c.type == rust.type && c.sequenceNumber == rust.sequenceNumber);
    assert(c.payloadLength == rust.payloadLength);
    if (c.payloadLength) assert(memcmp(c.payload,rust.payload,c.payloadLength)==0);
    int c_len=0, r_len=0;
    char *old=serializeRtspMessage(&c,&c_len);
    char *newer=pl_rtsp_serialize(&rust,&r_len,malloc);
    assert(old && newer && r_len==c_len && memcmp(old,newer,r_len)==0);
    free(old); free(newer);
    freeMessage(&c); freeMessage(&rust);
}
int main(void) {
    char buf[512];
    const char *methods[]={"OPTIONS","DESCRIBE","SETUP","ANNOUNCE","PLAY","TEARDOWN"};
    unsigned n=0;
    for (int i=0;i<400;++i) {
        for (int j=0;j<6;++j) {
            int bytes=snprintf(buf,sizeof(buf),"%s rtsp://127.0.0.1/video RTSP/1.0\r\nCSeq: %d\r\nTransport: client_port=45000-45001\r\nX: a:b\r\n\r\n",methods[j],i);
            memcpy(buf+bytes,"a\0bc",4);
            compare(buf,bytes+4); ++n;
        }
        int bytes=snprintf(buf,sizeof(buf),"RTSP/1.0 %d test reply\r\nCSeq: %d\r\n\r\n",200+i%4,i);
        for (int trim=0;trim<=2;++trim) {compare(buf,bytes-trim);++n;}
    }
    RTSP_MESSAGE r={0};
    const char extra[]="RTSP/1.0 200 OK\r\nCSeq: 1\r\nCSeq: 3\r\n\r\n\0bytes";
    assert(pl_rtsp_parse(&r,extra,sizeof(extra)-1,malloc,free)==0);
    assert(r.sequenceNumber==3 && r.payloadLength==6 && r.payload[0]==0);
    freeMessage(&r);
    const char normal[]="OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nTest: data\r\n\r\n";
    for(fail_on=1;fail_on<=3;++fail_on) {
        calls=live=0;
        assert(pl_rtsp_parse(&r,normal,sizeof(normal)-1,tracked_alloc,tracked_free)==-1);
        assert(live==0);
    }
    printf("%u canonical C/Rust comparisons passed, plus binary payload, duplicate header and allocation failures\n",n);
}
