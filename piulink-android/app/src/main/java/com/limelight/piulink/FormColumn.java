package com.limelight.piulink;

import android.content.Context;
import android.util.AttributeSet;
import android.widget.LinearLayout;

/** Keeps single-column forms readable as a tablet window rotates or resizes. */
public final class FormColumn extends LinearLayout {
    public FormColumn(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    @Override protected void onMeasure(int width, int height) {
        int limit = Math.round(608 * getResources().getDisplayMetrics().density);
        if (MeasureSpec.getMode(width) == MeasureSpec.UNSPECIFIED || MeasureSpec.getSize(width) > limit) {
            width = MeasureSpec.makeMeasureSpec(limit, MeasureSpec.EXACTLY);
        }
        super.onMeasure(width, height);
    }
}
