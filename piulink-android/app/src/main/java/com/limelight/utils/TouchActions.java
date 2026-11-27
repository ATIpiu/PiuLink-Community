package com.limelight.utils;

import android.app.Activity;
import android.app.AlertDialog;
import android.view.Gravity;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import com.limelight.R;
import java.util.function.IntUnaryOperator;

/** Uses the same menu items (including checked state and callbacks) as TV and keyboard menus. */
public final class TouchActions {
    private TouchActions() {}

    public static void show(Activity activity, CharSequence title, Menu menu, IntUnaryOperator iconFor,
                            int destructiveId, Runnable onDismiss, int... primaryIds) {
        LinearLayout list = new LinearLayout(activity);
        list.setOrientation(LinearLayout.VERTICAL);
        list.setPadding(dp(activity, 8), 0, dp(activity, 8), dp(activity, 8));
        ScrollView scroll = new ScrollView(activity);
        scroll.addView(list);
        AlertDialog dialog = new TouchDialog(activity).setTitle(title).setView(scroll).create();
        LinearLayout extra = new LinearLayout(activity);
        extra.setOrientation(LinearLayout.VERTICAL);
        for (int i = 0; i < menu.size(); i++) {
            MenuItem item = menu.getItem(i);
            if (!item.isVisible()) continue;
            int id = item.getItemId();
            boolean primary = false;
            for (int candidate : primaryIds) if (candidate == id) primary = true;
            TextView row = row(activity, item.getTitle(), iconFor.applyAsInt(id));
            if (id == destructiveId) row.setTextColor(activity.getColor(R.color.piulink_danger));
            if (item.isCheckable()) {
                row.setText(activity.getString(item.isChecked() ? R.string.dialog_option_on : R.string.dialog_option_off,
                        item.getTitle()));
            }
            row.setEnabled(item.isEnabled());
            row.setOnClickListener(v -> {
                dialog.dismiss();
                menu.performIdentifierAction(id, 0);
            });
            (primary ? list : extra).addView(row);
        }
        if (extra.getChildCount() > 0) {
            if (list.getChildCount() > 0) {
                View divider = new View(activity);
                divider.setBackgroundColor(activity.getColor(R.color.home_line));
                list.addView(divider, -1, dp(activity, 1));
                TextView more = row(activity, activity.getText(R.string.dialog_more_actions), R.drawable.ic_settings);
                list.addView(more);
                extra.setVisibility(View.GONE);
                more.setOnClickListener(v -> {
                    boolean expand = extra.getVisibility() != View.VISIBLE;
                    extra.setVisibility(expand ? View.VISIBLE : View.GONE);
                    more.setText(expand ? R.string.dialog_fewer_actions : R.string.dialog_more_actions);
                    if (!expand) scroll.scrollTo(0, 0);
                });
            }
            list.addView(extra);
        }
        if (onDismiss != null) dialog.setOnDismissListener(d -> onDismiss.run());
        dialog.show();
    }

    private static TextView row(Activity activity, CharSequence title, int icon) {
        TextView row = new Button(activity, null, android.R.attr.borderlessButtonStyle);
        row.setText(title);
        row.setAllCaps(false);
        row.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        row.setTextSize(15);
        row.setTextColor(activity.getColor(R.color.home_ink));
        row.setMinHeight(dp(activity, 48));
        row.setPaddingRelative(dp(activity, 14), dp(activity, 5), dp(activity, 14), dp(activity, 5));
        row.setBackgroundResource(R.drawable.bg_dialog_choice);
        android.graphics.drawable.Drawable drawable = activity.getDrawable(icon).mutate();
        drawable.setTint(activity.getColor(R.color.home_muted));
        drawable.setBounds(0, 0, dp(activity, 20), dp(activity, 20));
        row.setCompoundDrawablesRelative(drawable, null, null, null);
        row.setCompoundDrawablePadding(dp(activity, 14));
        return row;
    }

    private static int dp(Activity a, int n) { return TouchDialog.dp(a, n); }
}
