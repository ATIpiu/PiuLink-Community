#pragma once
#include <string.h>

// Access is serialized by the adapter's nonpaged spin lock. All sizes are bytes.
class PiuLinkPcmQueue
{
public:
    static constexpr unsigned Capacity = 7680; // 40 ms at 48 kHz, stereo, 16-bit PCM.

    void Clear() { head = count = 0; }

    void Write(const unsigned char* data, unsigned bytes)
    {
        if (bytes > Capacity) {
            data += bytes - Capacity;
            bytes = Capacity;
        }
        const unsigned tail = (head + count) % Capacity;
        const unsigned first = bytes < Capacity - tail ? bytes : Capacity - tail;
        memcpy(storage + tail, data, first);
        memcpy(storage, data + first, bytes - first);
        count += bytes;
        if (count > Capacity) {
            head = (head + count - Capacity) % Capacity;
            count = Capacity;
        }
    }

    unsigned Read(unsigned char* data, unsigned bytes)
    {
        const unsigned available = bytes < count ? bytes : count;
        const unsigned first = available < Capacity - head ? available : Capacity - head;
        memcpy(data, storage + head, first);
        memcpy(data + first, storage, available - first);
        head = (head + available) % Capacity;
        count -= available;
        return available;
    }

private:
    unsigned char storage[Capacity];
    unsigned head, count;
};
