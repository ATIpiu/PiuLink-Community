#include <time.h>
typedef struct { int32_t entries[2]; uint32_t length; } PlDisplayQueue;
typedef struct { int32_t (*next)(void *); bool (*release)(void *,int32_t,int32_t); } PlOutputOps;
extern int32_t pl_balanced_frame(PlDisplayQueue *,int32_t,int32_t,int64_t,int64_t,int32_t);
extern int64_t pl_present_latest(void *,int32_t,bool,const PlOutputOps *);
static jmethodID getOutput, discardOutput, presentOutput;
static jfieldID outputPts;

static void initOutputMethods(JNIEnv *env) {
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
