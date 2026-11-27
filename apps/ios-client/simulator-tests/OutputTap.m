// Record only the iOS simulator client process on the disposable macOS runner.
#import <Foundation/Foundation.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreAudio/AudioHardwareTapping.h>
#import <CoreAudio/CATapDescription.h>
#include <unistd.h>

static OSStatus capture(AudioObjectID device, const AudioTimeStamp *now, const AudioBufferList *in,
                        const AudioTimeStamp *inTime, AudioBufferList *out, const AudioTimeStamp *outTime, void *ctx) {
    if (in->mNumberBuffers && in->mBuffers[0].mData) {
        fwrite(in->mBuffers[0].mData, 1, in->mBuffers[0].mDataByteSize, ctx);
    }
    return noErr;
}
int main(int argc, const char **argv) {
    if (argc != 3) return 2;
    @autoreleasepool {
        pid_t pid = atoi(argv[1]);
        AudioObjectID proc = 0, tap = 0, aggregate = 0;
        AudioObjectPropertyAddress prop = {kAudioHardwarePropertyTranslatePIDToProcessObject, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain};
        UInt32 size = sizeof(proc);
        for (int i=0; i<50 && !proc; i++) {
            AudioObjectGetPropertyData(kAudioObjectSystemObject, &prop, sizeof(pid), &pid, &size, &proc);
            if (!proc) usleep(200000);
        }
        if (!proc) { fprintf(stderr,"No CoreAudio object for iOS process\n"); return 3; }
        CATapDescription *desc = [[CATapDescription alloc] initStereoMixdownOfProcesses:@[@(proc)]];
        desc.UUID = [NSUUID UUID];
        desc.name = @"PiuLink Simulator Output";
        desc.private = YES;
        desc.muteBehavior = CATapUnmuted;
        OSStatus rc = AudioHardwareCreateProcessTap(desc,&tap);
        if (rc) { fprintf(stderr,"Output tap unavailable: %d\n",(int)rc); return 4; }
        AudioStreamBasicDescription fmt = {0};
        prop.mSelector = kAudioTapPropertyFormat;
        size=sizeof(fmt);
        AudioObjectGetPropertyData(tap,&prop,0,NULL,&size,&fmt);
        fprintf(stderr,"iOS output tap rate %.0f channels %u flags %u bits %u\n",fmt.mSampleRate,(unsigned)fmt.mChannelsPerFrame,(unsigned)fmt.mFormatFlags,(unsigned)fmt.mBitsPerChannel);
        NSDictionary *cfg = @{@kAudioAggregateDeviceNameKey:@"PiuLink Output Check", @kAudioAggregateDeviceUIDKey:[NSUUID UUID].UUIDString,
          @kAudioAggregateDeviceIsPrivateKey:@YES,@kAudioAggregateDeviceTapAutoStartKey:@NO,
          @kAudioAggregateDeviceTapListKey:@[@{@kAudioSubTapUIDKey:desc.UUID.UUIDString,@kAudioSubTapDriftCompensationKey:@YES}]};
        rc = AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)cfg,&aggregate);
        if (rc) { fprintf(stderr,"Aggregate unavailable: %d\n",(int)rc); return 5; }
        FILE *file = fopen(argv[2],"wb");
        if (!file) return 6;
        AudioDeviceIOProcID io = NULL;
        rc=AudioDeviceCreateIOProcID(aggregate,capture,file,&io);
        if (!rc) rc=AudioDeviceStart(aggregate,io);
        if (!rc) sleep(12);
        if(io){ AudioDeviceStop(aggregate,io); AudioDeviceDestroyIOProcID(aggregate,io); }
        fclose(file);
        AudioHardwareDestroyAggregateDevice(aggregate);
        AudioHardwareDestroyProcessTap(tap);
        if(rc || fmt.mBitsPerChannel!=32 || !(fmt.mFormatFlags & kAudioFormatFlagIsFloat)) return 7;
        printf("%.0f %u\n",fmt.mSampleRate,(unsigned)((fmt.mFormatFlags & kAudioFormatFlagIsNonInterleaved)?1:fmt.mChannelsPerFrame));
    }
}
