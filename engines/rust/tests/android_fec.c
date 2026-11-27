#include "fec.h"
#include <stdio.h>

/* Read little-endian Windows host records on the Android target. */
int main(void) {
    uint32_t count = 0;
    for (unsigned i = 0; i < 1024; i++) {
        for (unsigned b = 0; b < 16; b++) {
            uint32_t info;
            uint8_t blocks;
            if (fread(&info, sizeof(info), 1, stdin) != 1 ||
                fread(&blocks, sizeof(blocks), 1, stdin) != 1) return 1;
            PlVideoFec decoded = pl_fec_read(info, blocks);
            if (decoded.index != i || decoded.data != 1023-i ||
                decoded.percentage != (uint8_t)i || decoded.current != (b & 3) ||
                decoded.last != (b >> 2)) return 2;
            count++;
        }
    }
    if (getchar() != EOF) return 3;
    printf("%u Windows host FEC records decoded on Android\n", count);
    return 0;
}
