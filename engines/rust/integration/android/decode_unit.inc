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
