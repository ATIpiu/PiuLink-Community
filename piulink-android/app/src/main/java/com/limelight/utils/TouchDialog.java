package com.limelight.utils;

import android.app.AlertDialog;
import android.content.Context;
import android.graphics.Typeface;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.widget.ImageButton;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.limelight.R;

/** Touch presentation around the platform dialog's existing actions and cancel callbacks. */
public final class TouchDialog extends AlertDialog.Builder {
    private CharSequence title;
    private View customTitle;

    public TouchDialog(Context context) {
        super(context, UiHelper.isTouchscreenClient(context) ? R.style.PiuLinkTouchDialogTheme : 0);
    }

    @Override public TouchDialog setTitle(CharSequence value) {
        title = value;
        super.setTitle(value);
        return this;
    }

    @Override public TouchDialog setTitle(int value) { return setTitle(getContext().getText(value)); }

    @Override public TouchDialog setCustomTitle(View view) {
        customTitle = view;
        super.setCustomTitle(view);
        return this;
    }

    @Override public AlertDialog create() {
        Context context = getContext();
        if (!UiHelper.isTouchscreenClient(context)) return super.create();
        ImageButton close = closeButton(context);
        super.setCustomTitle(header(context, title, customTitle, close));
        AlertDialog dialog = super.create();
        close.setOnClickListener(v -> dialog.cancel());
        fitOnShow(dialog);
        return dialog;
    }

    public static void prepareHeader(AlertDialog dialog, CharSequence title) {
        Context context = dialog.getContext();
        if (!UiHelper.isTouchscreenClient(context)) return;
        ImageButton close = closeButton(context);
        close.setOnClickListener(v -> dialog.cancel());
        dialog.setCustomTitle(header(context, title, null, close));
        fitOnShow(dialog);
    }

    public static void preferenceHeader(AlertDialog.Builder builder, android.preference.DialogPreference pref) {
        Context context = builder.getContext();
        if (!UiHelper.isTouchscreenClient(context)) return;
        ImageButton close = closeButton(context);
        close.setOnClickListener(v -> {
            android.app.Dialog dialog = pref.getDialog();
            if (dialog != null) dialog.cancel();
        });
        builder.setCustomTitle(header(context, pref.getDialogTitle(), null, close));
    }

    public static void fitOnShow(AlertDialog dialog) {
        Window window = dialog.getWindow();
        if (window == null) return;
        // Vendor AlertControllers can replace the window size when installing their panel.
        // Apply the size after installation without replacing the caller's onShow listener.
        View decor = window.getDecorView();
        Runnable resize = () -> {
            if (!dialog.isShowing()) return;
            int horizontal = window.getAttributes().gravity & Gravity.HORIZONTAL_GRAVITY_MASK;
            if (horizontal != Gravity.RIGHT && horizontal != Gravity.LEFT) fit(dialog, 368);
        };
        decor.post(resize);
        decor.addOnLayoutChangeListener(new View.OnLayoutChangeListener() {
            int previousWidthDp = decor.getResources().getConfiguration().screenWidthDp;
            @Override public void onLayoutChange(View view, int l, int t, int r, int b,
                                                 int oldL, int oldT, int oldR, int oldB) {
                int widthDp = view.getResources().getConfiguration().screenWidthDp;
                if (widthDp != previousWidthDp) {
                    previousWidthDp = widthDp;
                    view.post(resize);
                }
            }
        });
    }

    public static void fit(AlertDialog dialog, int maxWidthDp) {
        Context context = dialog.getContext();
        Window window = dialog.getWindow();
        if (window == null || !UiHelper.isTouchscreenClient(context)) return;
        int available = context.getResources().getConfiguration().screenWidthDp;
        if (available <= 0) available = Math.round(context.getResources().getDisplayMetrics().widthPixels
                / context.getResources().getDisplayMetrics().density);
        window.setBackgroundDrawableResource(R.drawable.bg_detail_sheet);
        window.setLayout(dp(context, Math.max(1, Math.min(maxWidthDp, available - 32))),
                ViewGroup.LayoutParams.WRAP_CONTENT);
        window.setDimAmount(0.38f);
    }

    public static ImageButton closeButton(Context context) {
        ImageButton button = new ImageButton(context);
        button.setId(R.id.dialogClose);
        button.setImageResource(R.drawable.ic_dialog_close);
        button.setContentDescription(context.getText(R.string.screen_close_panel));
        button.setBackgroundResource(R.drawable.bg_dialog_choice);
        int pad = dp(context, 13);
        button.setPadding(pad, pad, pad, pad);
        return button;
    }

    private static View header(Context context, CharSequence title, View custom, ImageButton close) {
        LinearLayout row = new LinearLayout(context);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPaddingRelative(dp(context, custom == null ? 20 : 0), dp(context, 4), dp(context, 6), 0);
        if (custom == null) {
            TextView label = new TextView(context);
            label.setText(title);
            label.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
            label.setMaxLines(2);
            label.setEllipsize(android.text.TextUtils.TruncateAt.END);
            label.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
            label.setTextColor(context.getColor(R.color.piulink_text_primary));
            custom = label;
        }
        row.addView(custom, new LinearLayout.LayoutParams(0, -2, 1));
        row.addView(close, dp(context, 48), dp(context, 48));
        return row;
    }

    static int dp(Context context, int value) {
        return Math.round(context.getResources().getDisplayMetrics().density * value);
    }
}
