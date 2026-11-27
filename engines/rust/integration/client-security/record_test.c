#include "rtsp_record.h"
#include "replay.h"
#include <assert.h>

static int split_record(const char* text, size_t split, bool eof) {
    PlRtspRecord state = {0};
    size_t size = strlen(text);
    int first = pl_rtsp_record(&state, (const unsigned char*)text, 0, split, false);
    return first != 0 ? first :
        pl_rtsp_record(&state, (const unsigned char*)text, split, size, eof);
}

int main(void) {
    const char* message = "RTSP/1.0 200 OK\r\ncOnTeNt-LeNgTh: 3\r\n\r\nabc";
    for (size_t split = 0; split <= strlen(message); split++)
        assert(split_record(message, split, false) == 1);
    assert(split_record("RTSP/1.0 200 OK\r\n\r\n", 2, false) == 0);
    assert(split_record("RTSP/1.0 200 OK\r\n\r\n", 2, true) == 1);
    assert(split_record("RTSP/1.0 200 OK\r\nContent-Length: 1\r\n\r\n", 0, true) == -1);
    assert(split_record("RTSP/1.0 200 OK\r\nContent-Length: 0\r\nContent-Length: 1\r\n\r\n", 0, false) == -1);
    unsigned char record[26] = {0x80, 0, 0, 2};
    PlRtspRecord state = {0};
    assert(pl_rtsp_record(&state, record, 0, 4, false) == 0);
    assert(pl_rtsp_record(&state, record, 4, sizeof(record), false) == 1);

    PlReplayWindow control = {0};
    assert(pl_accept_authenticated(&control, 65));
    assert(pl_accept_authenticated(&control, 64));
    assert(!pl_accept_authenticated(&control, 64));
    assert(!pl_accept_authenticated(&control, 1));
    return 0;
}
