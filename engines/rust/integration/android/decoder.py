from pathlib import Path


def integrate(project, core):
    src = core / "callbacks.c"
    code = src.read_text()
    anchor = "static void initOutputMethods(JNIEnv *env) {"
    src.write_text(code.replace(anchor, Path(__file__).with_suffix(".inc").read_text() + "\n" + anchor + "\n    initCodecMethod(env);"))
    java = project / "app/src/main/java/com/limelight"
    bridge = java / "nvstream/jni/MoonBridge.java"
    code = bridge.read_text()
    anchor = "    public static native void stopConnection();"
    bridge.write_text(code.replace(anchor, anchor + """
    public interface CodecBackend { int step(int operation, int value); }
    public static native int rustCodec(int kind, int argument, boolean option, CodecBackend backend);
"""))
    with (project / "app/proguard-rules.pro").open("a") as f:
        f.write("\n-keep interface com.limelight.nvstream.jni.MoonBridge$CodecBackend { *; }\n")
    renderer = java / "binding/video/MediaCodecDecoderRenderer.java"
    code = renderer.read_text()
    start = code.index("        for (int tryNumber = 0;; tryNumber++)")
    end = code.index("        if (Build.VERSION.SDK_INT", start)
    code = code[:start] + """        int configuration = MoonBridge.rustCodec(0, 0, false, (operation, attempt) -> {
            if (operation == 1) {
                maxDecodeWidth = initialWidth;
                maxDecodeHeight = initialHeight;
                return 0;
            }
            MediaFormat format = createBaseMediaFormat(mimeType);
            boolean more = MediaCodecHelper.setDecoderLowLatencyOptions(format, selectedDecoderInfo, attempt);
            boolean expanded = maxDecodeWidth != initialWidth || maxDecodeHeight != initialHeight;
            if (tryConfigureDecoder(selectedDecoderInfo, format, !expanded && !more && throwOnCodecError)) return 1;
            return more ? 2 : expanded ? 3 : 0;
        });
        if (configuration != 0) return configuration;

""" + code[end:]
    start = code.index("                // If we just need a flush")
    end = code.index("                // Wake all quiesced threads", start)
    code = code[:start] + """                try {
                    int result = MoonBridge.rustCodec(1, codecRecoveryType.get(),
                            Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP, (stage, ignored) -> {
                        if (stage == 0) {
                            codecRecoveryAttempts++;
                            LimeLog.info("Codec recovery attempt: " + codecRecoveryAttempts);
                            return 0;
                        }
                        try {
                            switch (stage) {
                                case 1: videoDecoder.flush(); break;
                                case 2: videoDecoder.stop(); configureAndStartDecoder(configuredFormat); break;
                                case 3: videoDecoder.reset(); configureAndStartDecoder(configuredFormat); break;
                                case 4:
                                    videoDecoder.release();
                                    int err = initializeDecoder(true);
                                    if (err != 0) throw new IllegalStateException("Decoder reset failed: " + err);
                                    break;
                                default: throw new AssertionError(stage);
                            }
                            return 0;
                        } catch (IllegalArgumentException e) {
                            if (stage == 1) throw e;
                            e.printStackTrace();
                            return 2;
                        } catch (IllegalStateException e) {
                            if (stage < 4) { e.printStackTrace(); return 1; }
                            if (!reportedCrash) { reportedCrash = true; crashListener.notifyCrash(e); }
                            throw new RendererException(this, e);
                        }
                    });
                    if (result == 2) stopping = true;
                } finally {
                    codecRecoveryType.set(CR_RECOVERY_TYPE_NONE);
                    codecRecoveryThreadQuiescedFlags = 0;
                    codecRecoveryMonitor.notifyAll();
                }
""" + code[code.index("            }\n            else {",end):]
    start = code.index("            // If we don't have an input buffer index")
    end = code.index("        } catch (IllegalStateException", start)
    code = code[:start] + """            nextInputBufferIndex = MoonBridge.rustCodec(2, nextInputBufferIndex, false, (op, index) -> {
                switch (op) {
                    case 0: return stopping ? 1 : 0;
                    case 1: return videoDecoder.dequeueInputBuffer(10000);
                    case 2:
                        nextInputBufferIndex = index;
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.LOLLIPOP) {
                            nextInputBuffer = videoDecoder.getInputBuffer(index);
                        } else {
                            nextInputBuffer = legacyInputBuffers[index];
                            nextInputBuffer.clear();
                        }
                        return nextInputBuffer == null ? 0 : 1;
                    default: throw new AssertionError(op);
                }
            });
""" + code[end:]
    start = code.index("            videoDecoder.queueInputBuffer(nextInputBufferIndex",code.index("    private boolean queueNextInputBuffer("))
    end = code.index("        } finally {",start)
    code = code[:start] + """            if (MoonBridge.rustCodec(3, 0, false, (op, ignored) -> {
                switch (op) {
                    case 0:
                        try {
                            videoDecoder.queueInputBuffer(nextInputBufferIndex, 0, nextInputBuffer.position(), timestampUs, codecFlags);
                            return 0;
                        } catch (IllegalStateException e) { return handleDecoderException(e) ? 1 : 2; }
                    case 1: nextInputBufferIndex = -1; nextInputBuffer = null; return 0;
                    case 2: nextInputBuffer.clear(); return 0;
                    default: throw new AssertionError(op);
                }
            }) != 1) return false;
""" + code[end:]
    renderer.write_text(code)
