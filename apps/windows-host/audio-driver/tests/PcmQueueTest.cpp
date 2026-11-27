#include "../PiuLinkPcmQueue.h"
#include <array>
#include <deque>
#include <random>

int main()
{
    PiuLinkPcmQueue queue;
    queue.Clear();
    std::deque<unsigned char> expected;
    std::array<unsigned char, PiuLinkPcmQueue::Capacity * 2> data{};
    std::mt19937 random(3481);
    for (unsigned i = 0; i < 5000; ++i) {
        unsigned size = random() % data.size();
        if (i % 29 == 0) { queue.Clear(); expected.clear(); }
        if (random() % 2 == 0) {
            for (unsigned j = 0; j < size; ++j) data[j] = static_cast<unsigned char>(random());
            queue.Write(data.data(), size);
            expected.insert(expected.end(), data.begin(), data.begin() + size);
            while (expected.size() > PiuLinkPcmQueue::Capacity) expected.pop_front();
        } else {
            auto used = queue.Read(data.data(), size);
            if (used != (size < expected.size() ? size : expected.size())) return 1;
            for (unsigned j = 0; j < used; ++j) {
                if (data[j] != expected.front()) return 1;
                expected.pop_front();
            }
        }
    }
}
