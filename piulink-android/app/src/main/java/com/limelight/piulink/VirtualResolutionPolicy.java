package com.limelight.piulink;

/** Chooses a remembered virtual display mode without depending on display enumeration order. */
public final class VirtualResolutionPolicy {
    private static final String RECOMMENDED = "2560x1440";
    private static final String SAFE_FALLBACK = "1920x1080";

    private VirtualResolutionPolicy() { }

    public static int preferredIndex(String[] modeIds, String rememberedMode) {
        if (modeIds == null || modeIds.length == 0) return -1;
        int recommended = -1;
        int safeFallback = -1;
        for (int i = 0; i < modeIds.length; i++) {
            if (modeIds[i].equals(rememberedMode)) return i;
            if (modeIds[i].equals(RECOMMENDED)) recommended = i;
            if (modeIds[i].equals(SAFE_FALLBACK)) safeFallback = i;
        }
        if (recommended >= 0) return recommended;
        if (safeFallback >= 0) return safeFallback;
        return 0;
    }
}
