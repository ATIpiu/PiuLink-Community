package com.limelight.preferences;

import android.app.AlertDialog;
import android.content.Context;
import android.preference.ListPreference;
import android.util.AttributeSet;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.CheckedTextView;

import com.limelight.R;
import com.limelight.piulink.FeaturedSettingView;
import com.limelight.utils.UiHelper;

/** A ListPreference whose choices remain readable and visibly focused on Android TV vendor themes. */
public class TvListPreference extends ListPreference {
    public TvListPreference(Context context, AttributeSet attrs) {
        super(context, attrs);
    }

    public TvListPreference(Context context) {
        super(context);
    }

    @Override protected void onBindView(View view) {
        super.onBindView(view);
        if (view instanceof FeaturedSettingView) ((FeaturedSettingView) view).bind(this);
        else MobilePreferenceSheet.value(view, getEntry());
    }

    @Override protected void showDialog(android.os.Bundle state) {
        super.showDialog(state);
        MobilePreferenceSheet.show((AlertDialog) getDialog());
    }

    public void chooseValue(String value) {
        if (isEnabled() && findIndexOfValue(value) >= 0 && callChangeListener(value)) setValue(value);
    }

    @Override
    protected void onPrepareDialogBuilder(AlertDialog.Builder builder) {
        com.limelight.utils.TouchDialog.preferenceHeader(builder, this);
        CharSequence[] entries = getEntries();
        CharSequence[] values = getEntryValues();
        if (entries == null || values == null) {
            super.onPrepareDialogBuilder(builder);
            return;
        }

        boolean touch = UiHelper.isTouchscreenClient(getContext());
        ArrayAdapter<CharSequence> adapter = new ArrayAdapter<CharSequence>(
                getContext(), touch ? R.layout.detail_sheet_option : android.R.layout.select_dialog_singlechoice, entries) {
            @Override
            public View getView(int position, View convertView, ViewGroup parent) {
                CheckedTextView view = (CheckedTextView) super.getView(position, convertView, parent);
                if (touch) return view;
                view.setTextColor(getContext().getColor(R.color.piulink_text_primary));
                view.setTextSize(18);
                view.setMinHeight(dp(64));
                view.setPadding(dp(24), 0, dp(24), 0);
                view.setBackgroundResource(R.drawable.bg_dialog_choice);
                return view;
            }
        };

        int selected = findIndexOfValue(getValue());
        builder.setSingleChoiceItems(adapter, selected, (dialog, which) -> {
            if (which >= 0 && which < values.length) {
                String value = values[which].toString();
                chooseValue(value);
            }
            dialog.dismiss();
        });
        builder.setPositiveButton(null, null);
    }

    private int dp(int value) {
        return Math.round(value * getContext().getResources().getDisplayMetrics().density);
    }
}
