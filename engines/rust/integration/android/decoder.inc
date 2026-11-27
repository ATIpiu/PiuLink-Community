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
