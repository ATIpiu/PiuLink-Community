package com.limelight.piulink;

import android.content.Context;
import android.graphics.Typeface;
import android.preference.CheckBoxPreference;
import android.preference.ListPreference;
import android.preference.Preference;
import android.util.AttributeSet;
import android.widget.Button;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.preferences.DetailCheckPreference;
import com.limelight.preferences.TvListPreference;
import java.util.Arrays;

/** Direct controls for a real Preference. Changes take its usual validation and dependency path. */
public final class FeaturedSettingView extends LinearLayout {
    private Preference preference;
    private MobileSettingsSection section;
    private QualityModeBar bar;
    private TextView value, detail;
    private CharSequence[] options;

    public FeaturedSettingView(Context context, AttributeSet attrs) { super(context, attrs); }

    @Override protected void onFinishInflate() {
        super.onFinishInflate();
        bar = findViewById(R.id.detailModeBar);
        bar.useLightSurface();
        value = findViewById(R.id.detailFeatureValue);
        detail = findViewById(R.id.detailFeatureDescription);
    }

    public void bind(Preference pref) {
        MobileSettingsSection next = MobileSettingsSection.forFeature(pref.getKey());
        if (next == null) return;
        CharSequence[] labels;
        if (pref instanceof ListPreference) {
            ListPreference list = (ListPreference) pref;
            CharSequence[] values = list.getEntryValues(), names = list.getEntries();
            if (values == null || names == null || values.length != names.length) return;
            labels = new CharSequence[values.length];
            for (int i = 0; i < labels.length; i++) labels[i] = next.option(getContext(), values[i].toString(), names[i]);
        } else {
            labels = new CharSequence[] {
                    getContext().getText(next.firstOn ? next.onLabel : next.offLabel),
                    getContext().getText(next.firstOn ? next.offLabel : next.onLabel)
            };
        }
        boolean rebuild = preference != pref || !Arrays.equals(options, labels);
        preference = pref;
        section = next;
        ((TextView) findViewById(android.R.id.title)).setText(section.field);
        if (rebuild) {
            ((ImageView) findViewById(R.id.detailFeatureImage)).setImageResource(section.art);
            options = labels;
            bar.removeAllViews();
            for (int i = 0; i < labels.length; i++) addOption(i);
        }
        update(false);
    }

    private void addOption(int i) {
        Button button = new Button(getContext());
        button.setText(options[i]);
        if (preference instanceof ListPreference) button.setContentDescription(((ListPreference) preference).getEntries()[i]);
        button.setAllCaps(false);
        button.setMinWidth(0);
        button.setMinimumWidth(0);
        button.setMinimumHeight(dp(48));
        button.setPadding(dp(3), dp(2), dp(3), dp(2));
        button.setTextSize(14);
        button.setTypeface(Typeface.create("sans-serif-medium", Typeface.NORMAL));
        button.setTextColor(0xff45463f);
        button.setStateListAnimator(null);
        button.setBackgroundResource(R.drawable.bg_settings_mode);
        button.setOnClickListener(v -> choose(i));
        bar.addView(button, new LayoutParams(0, -2, 1));
    }

    private void choose(int index) {
        if (!preference.isEnabled()) return;
        if (preference instanceof TvListPreference) {
            TvListPreference list = (TvListPreference) preference;
            String option = list.getEntryValues()[index].toString();
            if (!option.equals(list.getValue())) list.chooseValue(option);
        } else {
            ((DetailCheckPreference) preference).chooseChecked((index == 0) == section.firstOn);
        }
        update(true);
    }

    private void update(boolean animate) {
        int index;
        if (preference instanceof ListPreference) {
            ListPreference list = (ListPreference) preference;
            index = list.findIndexOfValue(list.getValue());
            value.setText(index >= 0 ? (section == MobileSettingsSection.ADVANCED ?
                    options[index] : list.getEntry()) : list.getValue());
            detail.setText(section.helpFor(list));
        } else {
            boolean checked = ((CheckBoxPreference) preference).isChecked();
            index = checked == section.firstOn ? 0 : 1;
            value.setText(checked ? section.onLabel : section.offLabel);
            detail.setText(checked ? section.onHelp : section.offHelp);
        }
        for (int i = 0; i < bar.getChildCount(); i++) bar.getChildAt(i).setEnabled(preference.isEnabled());
        bar.select(index, animate);
    }

    private int dp(int number) { return Math.round(number * getResources().getDisplayMetrics().density); }
}
