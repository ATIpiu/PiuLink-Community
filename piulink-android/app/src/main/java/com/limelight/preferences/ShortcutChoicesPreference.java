package com.limelight.preferences;

import android.app.AlertDialog;
import android.content.Context;
import android.os.Bundle;
import android.preference.MultiSelectListPreference;
import android.util.AttributeSet;
import android.view.View;
import java.util.Set;

/** Keeps Android's draft choices and confirmation semantics for the shortcut bar. */
public final class ShortcutChoicesPreference extends MultiSelectListPreference {
    public ShortcutChoicesPreference(Context context, AttributeSet attrs) { super(context, attrs); }

    @Override public void setValues(Set<String> values) {
        super.setValues(values);
        notifyChanged();
    }

    @Override protected void onBindView(View view) {
        super.onBindView(view);
        MobilePreferenceSheet.value(view, Integer.toString(getValues().size()));
    }

    @Override protected void showDialog(Bundle state) {
        super.showDialog(state);
        MobilePreferenceSheet.show((AlertDialog) getDialog());
    }
}
