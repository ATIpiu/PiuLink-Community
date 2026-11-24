package com.limelight.preferences;

import android.app.AlertDialog;
import android.content.Context;
import android.view.Gravity;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.ListView;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.utils.UiHelper;

final class MobilePreferenceSheet {
    static void show(AlertDialog dialog) {
        if (dialog == null) return;
        Context context = dialog.getContext();
        if (!UiHelper.isTouchscreenClient(context)) return;
        Window window = dialog.getWindow();
        if (window == null) return;
        float density = context.getResources().getDisplayMetrics().density;
        int available = context.getResources().getConfiguration().screenWidthDp;
        if (available <= 0) available = Math.round(context.getResources().getDisplayMetrics().widthPixels / density);
        boolean wide = available >= 650;
        window.setBackgroundDrawableResource(R.drawable.bg_detail_sheet);
        WindowManager.LayoutParams bounds = window.getAttributes();
        bounds.width = Math.round(Math.min(wide ? 460 : 490, Math.max(1, available - 24)) * density);
        bounds.gravity = wide ? Gravity.CENTER : Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL;
        bounds.y = wide ? 0 : Math.round(10 * density);
        bounds.dimAmount = 0.43f;
        window.setAttributes(bounds);
        ListView choices = dialog.getListView();
        if (choices != null) choices.setDivider(null);
    }

    static void value(View row, CharSequence current) {
        TextView field = row.findViewById(R.id.detailChoiceValue);
        if (field == null) return;
        field.setText(current);
        field.setVisibility(current == null || current.length() == 0 ? View.GONE : View.VISIBLE);
    }

    private MobilePreferenceSheet() { }
}
