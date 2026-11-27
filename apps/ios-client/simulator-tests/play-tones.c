#include <math.h>
#include <portaudio.h>
#include <stdio.h>
#include <string.h>

static int check(PaError error) {
    if (error == paNoError) return 1;
    fprintf(stderr, "PortAudio: %s\n", Pa_GetErrorText(error));
    return 0;
}

int main(void) {
    if (!check(Pa_Initialize())) return 1;
    PaStreamParameters out = {0};
    out.device = paNoDevice;
    for (int i = 0; i < Pa_GetDeviceCount(); i++) {
        const PaDeviceInfo *device = Pa_GetDeviceInfo(i);
        if (strcmp(device->name, "BlackHole 16ch") == 0 && device->maxOutputChannels == 16) {
            out.device = i;
            out.suggestedLatency = device->defaultHighOutputLatency;
            break;
        }
    }
    if (out.device == paNoDevice) return 2;
    out.channelCount = 16;
    out.sampleFormat = paFloat32;
    PaStream *stream = NULL;
    if (!check(Pa_OpenStream(&stream, NULL, &out, 48000, 480, paNoFlag, NULL, NULL))) return 3;
    if (!check(Pa_StartStream(stream))) return 4;
    const int tones[] = {523, 997, 733, 1319};
    float frame[480][16];
    for (int block = 0; block < 400; block++) {
        for (int n = 0; n < 480; n++) {
            float value = block % 100 < 65 ? 0.16 * sin(2 * 3.141592653589793 * tones[block / 100] * (block * 480 + n) / 48000.0) : 0;
            for (int ch = 0; ch < 16; ch++) frame[n][ch] = value;
        }
        PaError err = Pa_WriteStream(stream, frame, 480);
        if (err != paOutputUnderflowed && !check(err)) return 5;
    }
    if (!check(Pa_StopStream(stream))) return 6;
    Pa_CloseStream(stream);
    Pa_Terminate();
    puts("Played 192000 frames on BlackHole 16ch");
    return 0;
}
