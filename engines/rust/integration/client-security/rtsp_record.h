#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#define PL_RTSP_LIMIT (1024u * 1024u)

typedef struct {
    bool headerComplete;
    size_t expected; // Zero means the legacy server must close the connection.
} PlRtspRecord;

// Return -1 for an invalid record, 0 for more data, or 1 for a complete record.
static inline int pl_rtsp_record(PlRtspRecord* record, const unsigned char* data,
                                 size_t previous, size_t size, bool eof) {
    if (size > PL_RTSP_LIMIT) return -1;
    if (!record->headerComplete && size >= 4) {
        if (data[0] & 0x80) {
            uint32_t payload = ((uint32_t)(data[0] & 0x7f) << 24) |
                               ((uint32_t)data[1] << 16) | ((uint32_t)data[2] << 8) | data[3];
            if (payload > PL_RTSP_LIMIT - 24) return -1;
            record->expected = 24 + payload;
            record->headerComplete = true;
        } else {
            size_t end = previous > 3 ? previous - 3 : 0;
            while (end + 4 <= size && end + 4 <= 16384 && memcmp(data + end, "\r\n\r\n", 4)) end++;
            if (end + 4 > size || end + 4 > 16384) return eof || size >= 16384 ? -1 : 0;
            end += 4;
            size_t pos = 0;
            while (pos < end) {
                size_t line = pos;
                while (data[pos] != '\n') pos++;
                size_t stop = pos++;
                if (stop > line && data[stop - 1] == '\r') stop--;
                const char name[] = "content-length:";
                size_t i = 0;
                while (i < sizeof(name) - 1 && line + i < stop) {
                    unsigned c = data[line + i];
                    if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
                    if (c != (unsigned char)name[i]) break;
                    i++;
                }
                if (i != sizeof(name) - 1) continue;
                if (record->expected) return -1;
                size_t at = line + i, length = 0;
                while (at < stop && (data[at] == ' ' || data[at] == '\t')) at++;
                while (stop > at && (data[stop-1] == ' ' || data[stop-1] == '\t')) stop--;
                if (at == stop) return -1;
                for (; at < stop; at++) {
                    if (data[at] < '0' || data[at] > '9') return -1;
                    length = length * 10 + (data[at] - '0');
                    if (length > PL_RTSP_LIMIT - end) return -1;
                }
                record->expected = end + length;
            }
            record->headerComplete = true;
        }
    }
    if (!record->headerComplete) return eof ? -1 : 0;
    if (!record->expected) return eof ? 1 : 0;
    if (size > record->expected) return -1;
    if (size == record->expected) return 1;
    return eof ? -1 : 0;
}
