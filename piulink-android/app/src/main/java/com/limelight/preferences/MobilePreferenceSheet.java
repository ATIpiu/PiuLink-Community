package com.limelight.preferences;

import android.app.AlertDialog;
import android.content.Context;
import android.view.View;
import android.widget.ListView;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.utils.UiHelper;

final class MobilePreferenceSheet {
    static void show(AlertDialog dialog) {
        if (dialog == null) return;
        Context context = dialog.getContext();
        if (!UiHelper.isTouchscreenClient(context)) return;
        com.limelight.utils.TouchDialog.fitOnShow(dialog);
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
