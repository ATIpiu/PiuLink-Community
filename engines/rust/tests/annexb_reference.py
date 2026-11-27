"""Generate an Android native comparison with the original Moonlight Annex B parser."""
from pathlib import Path
import sys

source = Path(sys.argv[1]).read_text()
def extract(start, end):
    return source[source.index(start):source.index(end,source.index(start))]

header = """
#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#define VIDEO_FORMAT_MASK_H264 1
#define VIDEO_FORMAT_MASK_H265 2
#define LC_ASSERT(x) assert(x)
#define LC_ASSERT_VT(x) ((void)0)
#define BUFFER_TYPE_PICDATA 0
#define BUFFER_TYPE_SPS 1
#define BUFFER_TYPE_PPS 2
#define BUFFER_TYPE_VPS 3
static int NegotiatedVideoFormat;
typedef struct {unsigned offset,end; bool reference;} Fragment;
extern bool pl_nal_fragment(const char*,uint32_t,bool,Fragment*);
extern uint8_t pl_nal_kind(const char*,uint32_t,bool);
extern uint32_t pl_nal_next(const char*,uint32_t);
extern uint32_t pl_nal_leading(const char*,uint32_t,bool);
"""
stubs = """
typedef void* PLENTRY_INTERNAL;
static void *nalChainHead,*nalChainTail;
static bool waitingForIdrFrame,waitingForRefInvalFrame,waitingForNextSuccessfulFrame;
static int frameType;
#define FRAME_TYPE_IDR 1
static Fragment expected[32];
static int count;
static void queueFragment(PLENTRY_INTERNAL *entry,char* data,int start,int length) {
    assert(count<32);
    expected[count++]=(Fragment){start,start+length,entry!=NULL};
}
"""
main = """
static unsigned state=20;
static unsigned random_word(void) {state=1664525*state+1013904223;return state;}
int main(void) {
    char data[512];
    for(int codec=0;codec<2;codec++) {
        NegotiatedVideoFormat=codec?2:1;
        for(int trial=0;trial<6000;trial++) {
            unsigned n=0;
            int units=1+random_word()%12;
            for(int j=0;j<units;j++) {
                for(unsigned k=0,p=random_word()%4;k<p;k++)data[n++]=0;
                data[n++]=0;data[n++]=0;
                if(random_word()&1)data[n++]=0;
                data[n++]=1;
                unsigned t=random_word()>>(codec?26:27);
                // End on a reference slice so upstream slow-path preconditions hold.
                if(j==units-1)t=codec?19:5;
                data[n++]=codec?t<<1:t;
                for(unsigned k=0,m=random_word()%8;k<m;k++)data[n++]=(char)(1+random_word()%255);
            }
            BUFFER_DESC b={data,0,n};
            unsigned advance=pl_nal_leading(data,n,codec);
            if(!getAnnexBStartSequence(&b,NULL))skipToNextNal(&b);
            if(isAccessUnitDelimiter(&b))skipToNextNal(&b);
            while(isSeiNal(&b))skipToNextNal(&b);
            assert(advance==b.offset);
            count=0;
            PLENTRY_INTERNAL entry=NULL;
            processAvcHevcRtpPayloadSlow(&b,&entry);
            unsigned cursor=advance;
            for(int i=0;i<count;i++){
                Fragment f;
                assert(pl_nal_fragment(data+cursor,n-cursor,codec,&f));
                assert(cursor+f.offset==expected[i].offset && cursor+f.end==expected[i].end &&
                       f.reference==expected[i].reference);
                cursor+=f.end;
            }
            assert(cursor==n);
            for(unsigned cut=0;cut<=n;cut++){
                unsigned off=random_word()%(cut+1);
                BUFFER_DESC part={data,off,cut-off};
                uint8_t kind=pl_nal_kind(data+off,cut-off,codec);
                int flags=getBufferFlags(data+off,cut-off);
                assert(flags==(kind<=3?kind:0));
                skipToNextNalOrEnd(&part);
                assert(pl_nal_next(data+off,cut-off)==part.offset-off);
            }
        }
    }
    puts("12000 AVC/HEVC packets matched original fragment boundaries, reference flags and prefix handling");
}
"""
Path(sys.argv[2]).write_text(header + extract("typedef struct _BUFFER_DESC","typedef struct _LENTRY_INTERNAL") +
    extract("#define H264_NAL_TYPE(", "void initializeVideoDepacketizer") +
    extract("static bool getAnnexBStartSequence","void validateDecodeUnitForPlayback") +
    extract("static bool isSeqReferenceFrameStart","// Reassemble the frame with") +
    extract("static int getBufferFlags","// As an optimization") + stubs +
    extract("static void processAvcHevcRtpPayloadSlow","// Dumps the decode unit queue") + main)
