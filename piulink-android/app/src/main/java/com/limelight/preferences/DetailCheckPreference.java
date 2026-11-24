package com.limelight.preferences;

import android.content.Context;
import android.preference.CheckBoxPreference;
import android.util.AttributeSet;
import android.view.View;
import com.limelight.piulink.FeaturedSettingView;

/** Keeps the platform checkbox behavior on TV and the same preference on touch. */
public final class DetailCheckPreference extends CheckBoxPreference {
    public DetailCheckPreference(Context context, AttributeSet attrs) { super(context, attrs); }

    public void chooseChecked(boolean value) {
        if (isEnabled() && value != isChecked() && callChangeListener(value)) setChecked(value);
    }

    @Override protected void onBindView(View view) {
        super.onBindView(view);
        if (view instanceof FeaturedSettingView) ((FeaturedSettingView) view).bind(this);
    }
}
