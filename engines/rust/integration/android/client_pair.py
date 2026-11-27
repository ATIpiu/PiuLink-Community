from pathlib import Path


def integrate(project, core):
    here = Path(__file__).parent
    src = core / "callbacks.c"
    src.write_text(src.read_text() + "\n" + (here / "client_pair.inc").read_text())
    java = project / "app/src/main/java/com/limelight"
    bridge = java / "nvstream/jni/MoonBridge.java"
    anchor = "    public static native void stopConnection();"
    bridge.write_text(bridge.read_text().replace(anchor, anchor + """
    public interface PairBackend {
        byte[] step(int stage, byte[] data) throws java.io.IOException, org.xmlpull.v1.XmlPullParserException;
    }
    public static native int rustPair(boolean modern, byte[] pin, PairBackend backend)
            throws java.io.IOException, org.xmlpull.v1.XmlPullParserException;
"""))
    with (project / "app/proguard-rules.pro").open("a") as f:
        f.write("\n-keep interface com.limelight.nvstream.jni.MoonBridge$PairBackend { *; }\n")
    manager = java / "nvstream/http/PairingManager.java"
    code = manager.read_text()
    start = code.index("import org.bouncycastle")
    code = code[:start] + "import com.limelight.nvstream.jni.MoonBridge;\n" + code[code.index("import org.xmlpull",start):]
    for a,b in [("    private static byte[] saltPin","    private static Signature"),("    private static byte[] performBlockCipher","    private static byte[] concatBytes")]:
        code = code[:code.index(a)] + code[code.index(b):]
    manager.write_text(code[:code.index("    public PairState pair(String")] + (here / "client_pair.java.inc").read_text())
