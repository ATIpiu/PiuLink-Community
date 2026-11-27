"""Add a bounded audio measurement to the disposable Simulator build only."""
from pathlib import Path
import sys

p = Path(sys.argv[1]) / "Limelight/Stream/Connection.m"
source = p.read_text()
anchor = 'Log(LOG_E, @"Failed to queue audio sample: %s\\n", SDL_GetError());\n        }'
assert source.count(anchor) == 1
source = source.replace(anchor, anchor + ''' else {
#if TARGET_OS_SIMULATOR
            // This source exists only inside the temporary UI-test checkout.
            static FILE *samples;
            static size_t framesWritten;
            if (getenv("PIULINK_QA_AUDIO") && SDL_GetAudioDeviceStatus(audioDevice) == SDL_AUDIO_PLAYING &&
                framesWritten < (size_t)audioConfig.sampleRate * 45) {
                if (samples == NULL) {
                    NSString *directory = NSSearchPathForDirectoriesInDomains(NSCachesDirectory, NSUserDomainMask, YES).firstObject;
                    NSString *path = [directory stringByAppendingPathComponent:@"qa-played.s16"];
                    samples = fopen(path.fileSystemRepresentation, "wb");
                    NSString *format = [NSString stringWithFormat:@"%d %d", audioConfig.sampleRate, audioConfig.channelCount];
                    [format writeToFile:[directory stringByAppendingPathComponent:@"qa-played.format"]
                            atomically:YES encoding:NSUTF8StringEncoding error:nil];
                }
                if (samples != NULL) {
                    fwrite(audioBuffer, sizeof(short) * audioConfig.channelCount, decodeLen, samples);
                    framesWritten += decodeLen;
                    if (framesWritten % (audioConfig.sampleRate / 2) < (size_t)decodeLen) fflush(samples);
                }
            }
#endif
        }''')
p.write_text(source)
