package com.limelight.piulink;

import android.animation.ValueAnimator;
import android.os.Build;
import android.provider.Settings;
import android.view.View;
import android.view.animation.PathInterpolator;

/** Touch navigation feedback; page changes and focus are applied before the animation. */
final class MobilePageMotion {
    private static final PathInterpolator EASE = new PathInterpolator(0.2f, 0.75f, 0.2f, 1f);

    static boolean enabled(View view) {
        if (Build.VERSION.SDK_INT >= 26) return ValueAnimator.areAnimatorsEnabled();
        return Settings.Global.getFloat(view.getContext().getContentResolver(), Settings.Global.ANIMATOR_DURATION_SCALE, 1) > 0;
    }

    static void enter(View page, int direction) {
        page.animate().cancel();
        page.setTranslationX(0);
        page.setAlpha(1);
        if (!page.isAttachedToWindow() || !page.isShown() || !enabled(page)) return;
        if (page.getLayoutDirection() == View.LAYOUT_DIRECTION_RTL) direction = -direction;
        page.setTranslationX(direction * 14 * page.getResources().getDisplayMetrics().density);
        page.setAlpha(0.76f);
        page.animate().alpha(1).translationX(0).setDuration(185).setInterpolator(EASE).start();
    }

    private MobilePageMotion() { }
}
