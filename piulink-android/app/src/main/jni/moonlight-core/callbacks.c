#include "opus_stream.h"
#include <jni.h>

#include <pthread.h>
#include <string.h>

#include <Limelight.h>

#include <opus_multistream.h>
#include <android/log.h>

#include <cpu-features.h>

#include "PlatformSockets.h"

static PlOpusDecoder *Decoder;
static int PcmCapacity;
static void* createOpusBackend(const PlOpusFormat* f, int* error) {
    return opus_multistream_decoder_create(f->rate, f->channels, f->streams, f->coupled, f->mapping, error);
}
static int decodeOpusBackend(void* handle,const uint8_t* packet,int len,int16_t* pcm,int samples,int fec) {
    return opus_multistream_decode(handle,packet,len,pcm,samples,fec);
}
static void destroyOpusBackend(void* handle) { opus_multistream_decoder_destroy(handle); }
static const PlDecoderOps opusBackend = {createOpusBackend, decodeOpusBackend, destroyOpusBackend};

static JavaVM *JVM;
static pthread_key_t JniEnvKey;
static pthread_once_t JniEnvKeyInitOnce = PTHREAD_ONCE_INIT;
static jclass GlobalBridgeClass;
static jmethodID BridgeDrSetupMethod;
static jmethodID BridgeDrStartMethod;
static jmethodID BridgeDrStopMethod;
static jmethodID BridgeDrCleanupMethod;
static jmethodID BridgeDrSubmitDecodeUnitMethod;
static jmethodID BridgeArInitMethod;
static jmethodID BridgeArStartMethod;
static jmethodID BridgeArStopMethod;
static jmethodID BridgeArCleanupMethod;
static jmethodID BridgeArPlaySampleMethod;
static jmethodID BridgeClStageStartingMethod;
static jmethodID BridgeClStageCompleteMethod;
static jmethodID BridgeClStageFailedMethod;
static jmethodID BridgeClConnectionStartedMethod;
static jmethodID BridgeClConnectionTerminatedMethod;
static jmethodID BridgeClRumbleMethod;
static jmethodID BridgeClConnectionStatusUpdateMethod;
static jmethodID BridgeClSetHdrModeMethod;
static jmethodID BridgeClRumbleTriggersMethod;
static jmethodID BridgeClSetMotionEventStateMethod;
static jmethodID BridgeClSetControllerLEDMethod;
static jbyteArray DecodedFrameBuffer;
static jshortArray DecodedAudioBuffer;

void DetachThread(void* context) {
    (*JVM)->DetachCurrentThread(JVM);
}

void JniEnvKeyInit(void) {
    // Create a TLS slot for the JNIEnv. We aren't in
    // a pthread during init, so we must wait until we
    // are to initialize this.
    pthread_key_create(&JniEnvKey, DetachThread);
}

JNIEnv* GetThreadEnv(void) {
    JNIEnv* env;

    // First check if this is already attached to the JVM
    if ((*JVM)->GetEnv(JVM, (void**)&env, JNI_VERSION_1_4) == JNI_OK) {
        return env;
    }

    // Create the TLS slot now that we're safely in a pthread
    pthread_once(&JniEnvKeyInitOnce, JniEnvKeyInit);

    // Try the TLS to see if we already have a JNIEnv
    env = pthread_getspecific(JniEnvKey);
    if (env)
        return env;

    // This is the thread's first JNI call, so attach now
    (*JVM)->AttachCurrentThread(JVM, &env, NULL);

    // Write our JNIEnv to TLS, so we detach before dying
    pthread_setspecific(JniEnvKey, env);

    return env;
}

#include <time.h>
typedef struct { int32_t entries[2]; uint32_t length; } PlDisplayQueue;
typedef struct { int32_t (*next)(void *); bool (*release)(void *,int32_t,int32_t); } PlOutputOps;
extern int32_t pl_balanced_frame(PlDisplayQueue *,int32_t,int32_t,int64_t,int64_t,int32_t);
extern int64_t pl_present_latest(void *,int32_t,bool,const PlOutputOps *);
static jmethodID getOutput, discardOutput, presentOutput;
static jfieldID outputPts;

typedef struct { JNIEnv *env; jobject backend; jmethodID step; } PlCodecContext;
extern int32_t pl_codec_run(int32_t, int32_t, bool, void *, int32_t (*)(void *,int32_t,int32_t));
static int32_t codec_step(void *opaque, int32_t step, int32_t value) {
    PlCodecContext *c = opaque;
    JNIEnv *env = c->env;
    jint result = (*env)->CallIntMethod(env, c->backend, c->step, step, value);
    return (*env)->ExceptionCheck(env) ? INT32_MIN : result;
}
static jmethodID codecStepMethod;
static void initCodecMethod(JNIEnv *env) {
    jclass type = (*env)->FindClass(env, "com/limelight/nvstream/jni/MoonBridge$CodecBackend");
    if (type) {
        codecStepMethod = (*env)->GetMethodID(env, type, "step", "(II)I");
        (*env)->DeleteLocalRef(env, type);
    }
}
JNIEXPORT jint JNICALL Java_com_limelight_nvstream_jni_MoonBridge_rustCodec(
        JNIEnv *env, jclass klass, jint kind, jint arg, jboolean option, jobject backend) {
    PlCodecContext ctx = {env, backend, codecStepMethod};
    return pl_codec_run(kind, arg, option, &ctx, codec_step);
}

static void initOutputMethods(JNIEnv *env) {
    initCodecMethod(env);
    jclass codec=(*env)->FindClass(env,"android/media/MediaCodec");
    jclass info=(*env)->FindClass(env,"android/media/MediaCodec$BufferInfo");
    if (!codec || !info) return;
    getOutput=(*env)->GetMethodID(env,codec,"dequeueOutputBuffer","(Landroid/media/MediaCodec$BufferInfo;J)I");
    discardOutput=(*env)->GetMethodID(env,codec,"releaseOutputBuffer","(IZ)V");
    presentOutput=(*env)->GetMethodID(env,codec,"releaseOutputBuffer","(IJ)V");
    outputPts=(*env)->GetFieldID(env,info,"presentationTimeUs","J");
    (*env)->DeleteLocalRef(env,codec);
    (*env)->DeleteLocalRef(env,info);
}
JNIEXPORT jint JNICALL Java_com_limelight_nvstream_jni_MoonBridge_rustBalancedFrame(
        JNIEnv *env,jclass klass,jobject memory,jint op,jint index,jlong now,jlong last,jint fps) {
    if ((*env)->GetDirectBufferCapacity(env,memory) != sizeof(PlDisplayQueue)) return -1;
    PlDisplayQueue *queue=(*env)->GetDirectBufferAddress(env,memory);
    return queue ? pl_balanced_frame(queue,op,index,now,last,fps) : -1;
}

typedef struct { JNIEnv *env; jobject codec, info; jlong pts; } OutputContext;
static int32_t nextOutput(void *opaque) {
    OutputContext *c=opaque;
    JNIEnv *env=c->env;
    jint i=(*env)->CallIntMethod(env,c->codec,getOutput,c->info,(jlong)0);
    if ((*env)->ExceptionCheck(env)) return INT32_MIN;
    if (i>=0) c->pts=(*env)->GetLongField(env,c->info,outputPts);
    return i;
}
static bool releaseOutput(void *opaque,int32_t index,int32_t mode) {
    OutputContext *c=opaque;
    JNIEnv *env=c->env;
    if (mode==0) (*env)->CallVoidMethod(env,c->codec,discardOutput,index,(jboolean)JNI_FALSE);
    else {
        struct timespec ts={0};
        if (mode==2) clock_gettime(CLOCK_MONOTONIC,&ts);
        (*env)->CallVoidMethod(env,c->codec,presentOutput,index,(jlong)ts.tv_sec*1000000000+ts.tv_nsec);
    }
    return !(*env)->ExceptionCheck(env);
}
JNIEXPORT jlong JNICALL Java_com_limelight_nvstream_jni_MoonBridge_rustPresentLatest(
        JNIEnv *env,jclass klass,jobject codec,jobject info,jint first,jboolean keep) {
    static const PlOutputOps ops={nextOutput,releaseOutput};
    OutputContext c={env,codec,info,(*env)->GetLongField(env,info,outputPts)};
    jlong result=pl_present_latest(&c,first,keep,&ops);
    if (!(*env)->ExceptionCheck(env)) (*env)->SetLongField(env,info,outputPts,c.pts);
    return result;
}

JNIEXPORT void JNICALL
Java_com_limelight_nvstream_jni_MoonBridge_init(JNIEnv *env, jclass clazz) {
    initOutputMethods(env);
    (*env)->GetJavaVM(env, &JVM);
    GlobalBridgeClass = (*env)->NewGlobalRef(env, (*env)->FindClass(env, "com/limelight/nvstream/jni/MoonBridge"));
    BridgeDrSetupMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeDrSetup", "(IIII)I");
    BridgeDrStartMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeDrStart", "()V");
    BridgeDrStopMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeDrStop", "()V");
    BridgeDrCleanupMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeDrCleanup", "()V");
    BridgeDrSubmitDecodeUnitMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeDrSubmitDecodeUnit", "([BIIIICJJ)I");
    BridgeArInitMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeArInit", "(III)I");
    BridgeArStartMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeArStart", "()V");
    BridgeArStopMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeArStop", "()V");
    BridgeArCleanupMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeArCleanup", "()V");
    BridgeArPlaySampleMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeArPlaySample", "([S)V");
    BridgeClStageStartingMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClStageStarting", "(I)V");
    BridgeClStageCompleteMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClStageComplete", "(I)V");
    BridgeClStageFailedMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClStageFailed", "(II)V");
    BridgeClConnectionStartedMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClConnectionStarted", "()V");
    BridgeClConnectionTerminatedMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClConnectionTerminated", "(I)V");
    BridgeClRumbleMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClRumble", "(SSS)V");
    BridgeClConnectionStatusUpdateMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClConnectionStatusUpdate", "(I)V");
    BridgeClSetHdrModeMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClSetHdrMode", "(Z[B)V");
    BridgeClRumbleTriggersMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClRumbleTriggers", "(SSS)V");
    BridgeClSetMotionEventStateMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClSetMotionEventState", "(SBS)V");
    BridgeClSetControllerLEDMethod = (*env)->GetStaticMethodID(env, clazz, "bridgeClSetControllerLED", "(SBBB)V");
}

int BridgeDrSetup(int videoFormat, int width, int height, int redrawRate, void* context, int drFlags) {
    JNIEnv* env = GetThreadEnv();
    int err;

    err = (*env)->CallStaticIntMethod(env, GlobalBridgeClass, BridgeDrSetupMethod, videoFormat, width, height, redrawRate);
    if ((*env)->ExceptionCheck(env)) {
        // This is called on a Java thread, so it's safe to return
        return -1;
    }
    else if (err != 0) {
        return err;
    }

    DecodedFrameBuffer = NULL;  // Allocate on the first decode unit.
    return 0;
}

void BridgeDrStart(void) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeDrStartMethod);
}

void BridgeDrStop(void) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeDrStopMethod);
}

void BridgeDrCleanup(void) {
    JNIEnv* env = GetThreadEnv();

    if (DecodedFrameBuffer) (*env)->DeleteGlobalRef(env, DecodedFrameBuffer);
    DecodedFrameBuffer = NULL;

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeDrCleanupMethod);
}

typedef struct {
    bool (*reserve)(void *, int);
    bool (*write)(void *, int, const uint8_t *, int);
    int (*submit)(void *, int, int);
} PlDecoderSink;
extern int pl_submit_decode_unit(const LENTRY *, int, void *, const PlDecoderSink *);
typedef struct { JNIEnv *env; PDECODE_UNIT unit; } DecodeContext;

static bool reserveFrame(void *opaque, int length) {
    JNIEnv *env = ((DecodeContext *)opaque)->env;
    if (DecodedFrameBuffer && (*env)->GetArrayLength(env, DecodedFrameBuffer) >= length) return true;
    jbyteArray local = (*env)->NewByteArray(env, length);
    if (!local) return false;
    jbyteArray global = (*env)->NewGlobalRef(env, local);
    (*env)->DeleteLocalRef(env, local);
    if (!global) return false;
    if (DecodedFrameBuffer) (*env)->DeleteGlobalRef(env, DecodedFrameBuffer);
    DecodedFrameBuffer = global;
    return true;
}

static bool writeFrame(void *opaque, int offset, const uint8_t *bytes, int count) {
    JNIEnv *env = ((DecodeContext *)opaque)->env;
    (*env)->SetByteArrayRegion(env, DecodedFrameBuffer, offset, count, (const jbyte *)bytes);
    return !(*env)->ExceptionCheck(env);
}

static int submitFrame(void *opaque, int length, int kind) {
    DecodeContext *ctx = opaque;
    JNIEnv *env = ctx->env;
    PDECODE_UNIT u = ctx->unit;
    int ret = (*env)->CallStaticIntMethod(env, GlobalBridgeClass, BridgeDrSubmitDecodeUnitMethod,
          DecodedFrameBuffer, length, kind, u->frameNumber, u->frameType, (jchar)u->frameHostProcessingLatency,
          (jlong)u->receiveTimeMs, (jlong)u->enqueueTimeMs);
    return (*env)->ExceptionCheck(env) ? DR_NEED_IDR : ret;
}

int BridgeDrSubmitDecodeUnit(PDECODE_UNIT unit) {
    static const PlDecoderSink ops = {reserveFrame, writeFrame, submitFrame};
    JNIEnv *env = GetThreadEnv();
    DecodeContext ctx = {env, unit};
    int ret = pl_submit_decode_unit(unit->bufferList, unit->fullLength, &ctx, &ops);
    if ((*env)->ExceptionCheck(env)) {
        (*JVM)->DetachCurrentThread(JVM);
        return DR_OK;
    }
    return ret;
}

int BridgeArInit(int audioConfiguration, POPUS_MULTISTREAM_CONFIGURATION opusConfig, void* context, int flags) {
    JNIEnv* env = GetThreadEnv();
    int err;

    err = (*env)->CallStaticIntMethod(env, GlobalBridgeClass, BridgeArInitMethod, audioConfiguration, opusConfig->sampleRate, opusConfig->samplesPerFrame);
    if ((*env)->ExceptionCheck(env)) {
        // This is called on a Java thread, so it's safe to return
        err = -1;
    }
    if (err == 0) {
        PlOpusFormat format = {opusConfig->sampleRate, opusConfig->channelCount, opusConfig->streams,
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
    }

    return err;
}

void BridgeArStart(void) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArStartMethod);
}

void BridgeArStop(void) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArStopMethod);
}

void BridgeArCleanup() {
    JNIEnv* env = GetThreadEnv();

    pl_opus_decoder_free(Decoder);
    Decoder = NULL;

    (*env)->DeleteGlobalRef(env, DecodedAudioBuffer);

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArCleanupMethod);
}

void BridgeArDecodeAndPlaySample(char* sampleData, int sampleLength) {
    JNIEnv* env = GetThreadEnv();

    const int16_t* pcm = NULL;
    int count = pl_opus_decode(Decoder, (const uint8_t*)sampleData, sampleLength, &pcm);
    if (count <= 0) return;
    jshortArray output = count == PcmCapacity ? DecodedAudioBuffer : (*env)->NewShortArray(env, count);
    if (output) {
        (*env)->SetShortArrayRegion(env, output, 0, count, pcm);
        if (!(*env)->ExceptionCheck(env))
            (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeArPlaySampleMethod, output);
        if (output != DecodedAudioBuffer) (*env)->DeleteLocalRef(env, output);
    }
    if ((*env)->ExceptionCheck(env)) (*JVM)->DetachCurrentThread(JVM);
}

void BridgeClStageStarting(int stage) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClStageStartingMethod, stage);
}

void BridgeClStageComplete(int stage) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClStageCompleteMethod, stage);
}

void BridgeClStageFailed(int stage, int errorCode) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClStageFailedMethod, stage, errorCode);
}

void BridgeClConnectionStarted(void) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClConnectionStartedMethod);
}

void BridgeClConnectionTerminated(int errorCode) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClConnectionTerminatedMethod, errorCode);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
    }
}

void BridgeClRumble(unsigned short controllerNumber, unsigned short lowFreqMotor, unsigned short highFreqMotor) {
    JNIEnv* env = GetThreadEnv();

    // The seemingly redundant short casts are required in order to convert the unsigned short to a signed short.
    // If we leave it as an unsigned short, CheckJNI will fail when the value exceeds 32767. The cast itself is
    // fine because the Java code treats the value as unsigned even though it's stored in a signed type.
    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClRumbleMethod, controllerNumber, (short)lowFreqMotor, (short)highFreqMotor);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
    }
}

void BridgeClConnectionStatusUpdate(int connectionStatus) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClConnectionStatusUpdateMethod, connectionStatus);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
        return;
    }
}

void BridgeClSetHdrMode(bool enabled) {
    JNIEnv* env = GetThreadEnv();

    jbyteArray hdrMetadataByteArray = NULL;
    SS_HDR_METADATA hdrMetadata;

    // Check if HDR metadata was provided
    if (enabled && LiGetHdrMetadata(&hdrMetadata)) {
        hdrMetadataByteArray = (*env)->NewByteArray(env, sizeof(SS_HDR_METADATA));
        (*env)->SetByteArrayRegion(env, hdrMetadataByteArray, 0, sizeof(SS_HDR_METADATA), (jbyte*)&hdrMetadata);
    }

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClSetHdrModeMethod, enabled, hdrMetadataByteArray);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
    }
}

void BridgeClRumbleTriggers(unsigned short controllerNumber, unsigned short leftTrigger, unsigned short rightTrigger) {
    JNIEnv* env = GetThreadEnv();

    // The seemingly redundant short casts are required in order to convert the unsigned short to a signed short.
    // If we leave it as an unsigned short, CheckJNI will fail when the value exceeds 32767. The cast itself is
    // fine because the Java code treats the value as unsigned even though it's stored in a signed type.
    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClRumbleTriggersMethod, controllerNumber, (short)leftTrigger, (short)rightTrigger);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
    }
}

void BridgeClSetMotionEventState(uint16_t controllerNumber, uint8_t motionType, uint16_t reportRateHz) {
    JNIEnv* env = GetThreadEnv();

    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClSetMotionEventStateMethod, controllerNumber, motionType, reportRateHz);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
    }
}

void BridgeClSetControllerLED(uint16_t controllerNumber, uint8_t r, uint8_t g, uint8_t b) {
    JNIEnv* env = GetThreadEnv();

    // These jbyte casts are necessary to satisfy CheckJNI
    (*env)->CallStaticVoidMethod(env, GlobalBridgeClass, BridgeClSetControllerLEDMethod, controllerNumber, (jbyte)r, (jbyte)g, (jbyte)b);
    if ((*env)->ExceptionCheck(env)) {
        // We will crash here
        (*JVM)->DetachCurrentThread(JVM);
    }
}

void BridgeClLogMessage(const char* format, ...) {
    va_list va;
    va_start(va, format);
    __android_log_vprint(ANDROID_LOG_INFO, "moonlight-common-c", format, va);
    va_end(va);
}

static DECODER_RENDERER_CALLBACKS BridgeVideoRendererCallbacks = {
        .setup = BridgeDrSetup,
        .start = BridgeDrStart,
        .stop = BridgeDrStop,
        .cleanup = BridgeDrCleanup,
        .submitDecodeUnit = BridgeDrSubmitDecodeUnit,
};

static AUDIO_RENDERER_CALLBACKS BridgeAudioRendererCallbacks = {
        .init = BridgeArInit,
        .start = BridgeArStart,
        .stop = BridgeArStop,
        .cleanup = BridgeArCleanup,
        .decodeAndPlaySample = BridgeArDecodeAndPlaySample,
        .capabilities = CAPABILITY_SUPPORTS_ARBITRARY_AUDIO_DURATION
};

static CONNECTION_LISTENER_CALLBACKS BridgeConnListenerCallbacks = {
        .stageStarting = BridgeClStageStarting,
        .stageComplete = BridgeClStageComplete,
        .stageFailed = BridgeClStageFailed,
        .connectionStarted = BridgeClConnectionStarted,
        .connectionTerminated = BridgeClConnectionTerminated,
        .logMessage = BridgeClLogMessage,
        .rumble = BridgeClRumble,
        .connectionStatusUpdate = BridgeClConnectionStatusUpdate,
        .setHdrMode = BridgeClSetHdrMode,
        .rumbleTriggers = BridgeClRumbleTriggers,
        .setMotionEventState = BridgeClSetMotionEventState,
        .setControllerLED = BridgeClSetControllerLED,
};

static bool
hasFastAes() {
    if (android_getCpuCount() <= 2) {
        return false;
    }

    switch (android_getCpuFamily()) {
        case ANDROID_CPU_FAMILY_ARM:
            return !!(android_getCpuFeatures() & ANDROID_CPU_ARM_FEATURE_AES);
        case ANDROID_CPU_FAMILY_ARM64:
            return !!(android_getCpuFeatures() & ANDROID_CPU_ARM64_FEATURE_AES);
        case ANDROID_CPU_FAMILY_X86:
        case ANDROID_CPU_FAMILY_X86_64:
            return !!(android_getCpuFeatures() & ANDROID_CPU_X86_FEATURE_AES_NI);
        case ANDROID_CPU_FAMILY_MIPS:
        case ANDROID_CPU_FAMILY_MIPS64:
            return false;
        default:
            // Assume new architectures will all have crypto acceleration (RISC-V will)
            return true;
    }
}

JNIEXPORT jint JNICALL
Java_com_limelight_nvstream_jni_MoonBridge_startConnection(JNIEnv *env, jclass clazz,
                                                           jstring address, jstring appVersion, jstring gfeVersion,
                                                           jstring rtspSessionUrl, jint serverCodecModeSupport,
                                                           jint width, jint height, jint fps,
                                                           jint bitrate, jint packetSize, jint streamingRemotely,
                                                           jint audioConfiguration, jint supportedVideoFormats,
                                                           jint clientRefreshRateX100,
                                                           jbyteArray riAesKey, jbyteArray riAesIv,
                                                           jint videoCapabilities,
                                                           jint colorSpace, jint colorRange) {
    SERVER_INFORMATION serverInfo = {
            .address = (*env)->GetStringUTFChars(env, address, 0),
            .serverInfoAppVersion = (*env)->GetStringUTFChars(env, appVersion, 0),
            .serverInfoGfeVersion = gfeVersion ? (*env)->GetStringUTFChars(env, gfeVersion, 0) : NULL,
            .rtspSessionUrl = rtspSessionUrl ? (*env)->GetStringUTFChars(env, rtspSessionUrl, 0) : NULL,
            .serverCodecModeSupport = serverCodecModeSupport,
    };
    STREAM_CONFIGURATION streamConfig = {
            .width = width,
            .height = height,
            .fps = fps,
            .bitrate = bitrate,
            .packetSize = packetSize,
            .streamingRemotely = streamingRemotely,
            .audioConfiguration = audioConfiguration,
            .supportedVideoFormats = supportedVideoFormats,
            .clientRefreshRateX100 = clientRefreshRateX100,
            .encryptionFlags = ENCFLG_AUDIO,
            .colorSpace = colorSpace,
            .colorRange = colorRange
    };

    jbyte* riAesKeyBuf = (*env)->GetByteArrayElements(env, riAesKey, NULL);
    memcpy(streamConfig.remoteInputAesKey, riAesKeyBuf, sizeof(streamConfig.remoteInputAesKey));
    (*env)->ReleaseByteArrayElements(env, riAesKey, riAesKeyBuf, JNI_ABORT);

    jbyte* riAesIvBuf = (*env)->GetByteArrayElements(env, riAesIv, NULL);
    memcpy(streamConfig.remoteInputAesIv, riAesIvBuf, sizeof(streamConfig.remoteInputAesIv));
    (*env)->ReleaseByteArrayElements(env, riAesIv, riAesIvBuf, JNI_ABORT);

    BridgeVideoRendererCallbacks.capabilities = videoCapabilities;

    // Enable all encryption features if the platform has fast AES support
    if (hasFastAes()) {
        streamConfig.encryptionFlags = ENCFLG_ALL;
    }

    int ret = LiStartConnection(&serverInfo,
                                &streamConfig,
                                &BridgeConnListenerCallbacks,
                                &BridgeVideoRendererCallbacks,
                                &BridgeAudioRendererCallbacks,
                                NULL, 0,
                                NULL, 0);

    (*env)->ReleaseStringUTFChars(env, address, serverInfo.address);
    (*env)->ReleaseStringUTFChars(env, appVersion, serverInfo.serverInfoAppVersion);
    if (gfeVersion != NULL) {
        (*env)->ReleaseStringUTFChars(env, gfeVersion, serverInfo.serverInfoGfeVersion);
    }
    if (rtspSessionUrl != NULL) {
        (*env)->ReleaseStringUTFChars(env, rtspSessionUrl, serverInfo.rtspSessionUrl);
    }

    return ret;
}

JNIEXPORT void JNICALL
Java_com_limelight_nvstream_jni_MoonBridge_setNetworkHandle(JNIEnv *env, jclass clazz,
                                                            jlong networkHandle) {
    setSocketNetworkHandle((uint64_t)networkHandle);
}

typedef struct { JNIEnv *env; jobject backend; jmethodID method; } PairClientContext;
extern int32_t pl_client_pair(bool,const uint8_t *,size_t,void *,
        int32_t (*)(void *,int32_t,const uint8_t *,size_t,uint8_t *,size_t));
static int32_t pairClientStep(void *opaque,int32_t stage,const uint8_t *in,size_t count,uint8_t *out,size_t cap) {
    PairClientContext *ctx=opaque;
    JNIEnv *env=ctx->env;
    jbyteArray input=(*env)->NewByteArray(env,count);
    if (!input) return -1;
    if (count) (*env)->SetByteArrayRegion(env,input,0,count,(const jbyte *)in);
    jbyteArray result=NULL;
    if (!(*env)->ExceptionCheck(env)) result=(*env)->CallObjectMethod(env,ctx->backend,ctx->method,stage,input);
    (*env)->DeleteLocalRef(env,input);
    if (!result || (*env)->ExceptionCheck(env)) return -1;
    jsize length=(*env)->GetArrayLength(env,result);
    if (length>0 && (size_t)length<=cap) (*env)->GetByteArrayRegion(env,result,0,length,(jbyte *)out);
    (*env)->DeleteLocalRef(env,result);
    return (size_t)length<=cap && !(*env)->ExceptionCheck(env) ? length : -1;
}
JNIEXPORT jint JNICALL Java_com_limelight_nvstream_jni_MoonBridge_rustPair(
        JNIEnv *env,jclass klass,jboolean modern,jbyteArray pin,jobject backend) {
    if ((*env)->GetArrayLength(env,pin)!=4) return 3;
    jbyte digits[4];
    (*env)->GetByteArrayRegion(env,pin,0,4,digits);
    jclass type=(*env)->GetObjectClass(env,backend);
    if (!type) return 3;
    jmethodID method=(*env)->GetMethodID(env,type,"step","(I[B)[B");
    (*env)->DeleteLocalRef(env,type);
    if (!method) return 3;
    PairClientContext ctx={env,backend,method};
    return pl_client_pair(modern,(const uint8_t *)digits,4,&ctx,pairClientStep);
}
