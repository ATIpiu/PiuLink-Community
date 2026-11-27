from pathlib import Path


def integrate(project, core):
    source = core / "callbacks.c"
    code = source.read_text()
    anchor = "JNIEXPORT void JNICALL\nJava_com_limelight_nvstream_jni_MoonBridge_init"
    code = code.replace(anchor, Path(__file__).with_suffix(".inc").read_text() + "\n" + anchor)
    source.write_text(code.replace("    (*env)->GetJavaVM(env, &JVM);", "    initOutputMethods(env);\n    (*env)->GetJavaVM(env, &JVM);"))
    java = project / "app/src/main/java/com/limelight"
    bridge = java / "nvstream/jni/MoonBridge.java"
    code = bridge.read_text()
    anchor = "    public static native void stopConnection();"
    bridge.write_text(code.replace(anchor, anchor + """
    public static native int rustBalancedFrame(java.nio.ByteBuffer queue, int operation, int index,
                                                long now, long last, int fps);
    public static native long rustPresentLatest(android.media.MediaCodec codec, android.media.MediaCodec.BufferInfo info,
                                                 int first, boolean keep);
"""))
    renderer = java / "binding/video/MediaCodecDecoderRenderer.java"
    code = renderer.read_text()
    start = code.index("    private LinkedBlockingQueue<Integer> outputBufferQueue")
    end = code.index("    private long lastRendered", start)
    code = code[:start] + """    private final ByteBuffer outputBufferQueue = ByteBuffer.allocateDirect(12);
    private int updateOutputQueue(int operation, int index, long timestamp) {
        synchronized (outputBufferQueue) {
            return MoonBridge.rustBalancedFrame(outputBufferQueue, operation, index, timestamp,
                                                 lastRenderedFrameTimeNanos, refreshRate);
        }
    }
""" + code[end:]
    code = code.replace("outputBufferQueue.clear();", "updateOutputQueue(0, -1, 0);")
    start = code.index("        long actualFrameTimeDeltaNs")
    end = code.index("                try {",start)
    code = code[:start]+"""        {
            int nextOutputBuffer = updateOutputQueue(2, -1, frameTimeNanos);
            if (nextOutputBuffer >= 0) {
"""+code[end:]
    start = code.index("                                // Get the last output buffer")
    end = code.index("                                activeWindowVideoStats.totalFramesRendered",start)
    code = code[:start]+"""                                boolean keep = prefs.framePacing == PreferenceConfiguration.FRAME_PACING_MAX_SMOOTHNESS ||
                                               prefs.framePacing == PreferenceConfiguration.FRAME_PACING_CAP_FPS;
                                long result = MoonBridge.rustPresentLatest(videoDecoder, info, lastIndex, keep);
                                numFramesOut += (int)(result >>> 32) - 1;
                                presentationTimeUs = info.presentationTimeUs;
                                if ((int)result == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) readOutputSize();

"""+code[end:]
    start = code.index("                                // For balanced frame pacing case")
    end = code.index("outputBufferQueue.add(lastIndex);",start)+len("outputBufferQueue.add(lastIndex);")
    renderer.write_text(code[:start]+"""                                int displaced = updateOutputQueue(1, lastIndex, 0);
                                if (displaced >= 0) videoDecoder.releaseOutputBuffer(displaced, false);"""+code[end:])
