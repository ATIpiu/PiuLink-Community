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
