from pathlib import Path
import shutil

def integrate(core, wire):
    shutil.copy2(wire / "opus_stream.h", core)
    path = core / "callbacks.c"
    code = '#include "opus_stream.h"\n' + path.read_text()
    start = code.index("static OpusMSDecoder* Decoder;")
    end = code.index("static JavaVM",start)
    code = code[:start] + "static PlOpusDecoder *Decoder;\nstatic int PcmCapacity;\n" + Path(__file__).with_name("opus_backend.inc").read_text() + "\n" + code[end:]
    start = code.index("        memcpy(&OpusConfig")
    end = code.index("    }\n\n    return err;",start)
    code = code[:start] + """        PlOpusFormat format = {opusConfig->sampleRate, opusConfig->channelCount, opusConfig->streams,
                               opusConfig->coupledStreams, opusConfig->samplesPerFrame, {0}};
        memcpy(format.mapping, opusConfig->mapping, sizeof(format.mapping));
        Decoder = pl_opus_decoder_new(&format, &opusBackend, &err);
        if (Decoder == NULL) {
            (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArCleanupMethod);
            return err ? err : -1;
        }
        PcmCapacity = format.samples * format.channels;
        jshortArray local = (*env)->NewShortArray(env, PcmCapacity);
        DecodedAudioBuffer = local ? (*env)->NewGlobalRef(env, local) : NULL;
        if (local) (*env)->DeleteLocalRef(env, local);
        if (!DecodedAudioBuffer) {
            pl_opus_decoder_free(Decoder);
            Decoder = NULL;
            // Propagate the Java allocation exception after releasing Java's audio track.
            jthrowable thrown = (*env)->ExceptionOccurred(env);
            if (thrown) (*env)->ExceptionClear(env);
            (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArCleanupMethod);
            if (thrown) (*env)->Throw(env, thrown);
            return -1;
        }
""" + code[end:]
    code = code.replace("    opus_multistream_decoder_destroy(Decoder);", "    pl_opus_decoder_free(Decoder);\n    Decoder = NULL;")
    start = code.index("    jshort* decodedData =")
    end = code.index("\n}",start)
    code = code[:start] + """    const int16_t* pcm = NULL;
    int count = pl_opus_decode(Decoder, (const uint8_t*)sampleData, sampleLength, &pcm);
    if (count <= 0) return;
    jshortArray output = count == PcmCapacity ? DecodedAudioBuffer : (*env)->NewShortArray(env, count);
    if (output) {
        (*env)->SetShortArrayRegion(env, output, 0, count, pcm);
        if (!(*env)->ExceptionCheck(env))
            (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArPlaySampleMethod, output);
        if (output != DecodedAudioBuffer) (*env)->DeleteLocalRef(env, output);
    }
    if ((*env)->ExceptionCheck(env)) (*JVM)->DetachCurrentThread(JVM);""" + code[end:]
    path.write_text(code)
