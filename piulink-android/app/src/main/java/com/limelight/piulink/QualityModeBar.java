package com.limelight.piulink;

import android.animation.ArgbEvaluator;
import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.util.AttributeSet;
import android.view.View;
import android.view.animation.PathInterpolator;
import android.widget.Button;
import android.widget.LinearLayout;

/** Keeps real, individually focusable buttons above one moving selection indicator. */
public final class QualityModeBar extends LinearLayout {
    private int ink = Color.rgb(23, 24, 25);
    private int muted = Color.rgb(200, 199, 194);
    private final Paint pill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final ArgbEvaluator colors = new ArgbEvaluator();
    private final float radius;
    private ValueAnimator motion;
    private int selected = -1;
    private float left, right;

    public QualityModeBar(Context context, AttributeSet attrs) {
        super(context, attrs);
        setWillNotDraw(false);
        pill.setColor(Color.rgb(233, 230, 224));
        radius = 10 * getResources().getDisplayMetrics().density;
    }

    void useLightSurface() {
        pill.setColor(Color.rgb(42, 44, 43));
        ink = Color.rgb(248, 247, 241);
        muted = Color.rgb(66, 68, 64);
    }

    @Override public void onViewRemoved(View child) {
        super.onViewRemoved(child);
        stopMotion();
        selected = -1;
        left = right = 0;
    }

    void select(int index, boolean animate) {
        if (index == selected) return;
        boolean hasStart = selected >= 0 && right > left;
        selected = index;
        stopMotion();
        for (int i = 0; i < getChildCount(); i++) getChildAt(i).setSelected(i == index);
        View target = getChildAt(index);
        if (target == null || target.getWidth() == 0) {
            left = right = 0;
            invalidate();
            return;
        }
        if (!animate || !hasStart || !isShown() || !MobilePageMotion.enabled(this)) {
            place(target.getLeft(), target.getRight());
            return;
        }

        float fromLeft = left, fromRight = right;
        float toLeft = target.getLeft(), toRight = target.getRight();
        motion = ValueAnimator.ofFloat(0f, 1f);
        motion.setDuration(210);
        motion.setInterpolator(new PathInterpolator(0.2f, 0.75f, 0.2f, 1f));
        motion.addUpdateListener(animation -> {
            float t = (float) animation.getAnimatedValue();
            place(fromLeft + (toLeft - fromLeft) * t, fromRight + (toRight - fromRight) * t);
        });
        motion.start();
    }

    private void place(float start, float end) {
        left = start;
        right = end;
        for (int i = 0; i < getChildCount(); i++) {
            Button button = (Button) getChildAt(i);
            float center = (button.getLeft() + button.getRight()) / 2f;
            float edge = Math.min(center - left, right - center);
            float transition = Math.max(1f, button.getTextSize());
            float coverage = Math.max(0f, Math.min(1f, 0.5f + edge / transition));
            int color = (int) colors.evaluate(coverage, muted, ink);
            if (button.getCurrentTextColor() != color) button.setTextColor(color);
        }
        invalidate();
    }

    @Override protected void onLayout(boolean changed, int l, int t, int r, int b) {
        super.onLayout(changed, l, t, r, b);
        View target = getChildAt(selected);
        if (target != null && (changed || right <= left || motion == null || !motion.isRunning())) {
            stopMotion();
            place(target.getLeft(), target.getRight());
        }
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        View target = getChildAt(selected);
        if (target != null && right > left) canvas.drawRoundRect(left, target.getTop(), right, target.getBottom(), radius, radius, pill);
    }

    private void stopMotion() {
        if (motion != null) { motion.cancel(); motion = null; }
    }

    @Override protected void onDetachedFromWindow() {
        stopMotion();
        super.onDetachedFromWindow();
    }
}
