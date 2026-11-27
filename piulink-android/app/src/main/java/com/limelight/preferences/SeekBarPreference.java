package com.limelight.preferences;

import android.app.AlertDialog;
import android.content.Context;
import android.os.Bundle;
import android.preference.DialogPreference;
import android.util.AttributeSet;
import android.view.Gravity;
import android.view.View;
import android.view.View.OnClickListener;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

import java.util.Locale;

// Based on a Stack Overflow example: http://stackoverflow.com/questions/1974193/slider-on-my-preferencescreen
public class SeekBarPreference extends DialogPreference
{
    private static final String ANDROID_SCHEMA_URL = "http://schemas.android.com/apk/res/android";
    private static final String SEEKBAR_SCHEMA_URL = "http://schemas.moonlight-stream.com/apk/res/seekbar";

    private SeekBar seekBar;
    private TextView valueText;
    private final Context context;

    private final String dialogMessage;
    private final String suffix;
    private final int defaultValue;
    private final int maxValue;
    private final int minValue;
    private final int stepSize;
    private final int keyStepSize;
    private final int divisor;
    private int currentValue;

    public SeekBarPreference(Context context, AttributeSet attrs) {
        super(context, attrs);
        this.context = context;

        // Read the message from XML
        int dialogMessageId = attrs.getAttributeResourceValue(ANDROID_SCHEMA_URL, "dialogMessage", 0);
        if (dialogMessageId == 0) {
            dialogMessage = attrs.getAttributeValue(ANDROID_SCHEMA_URL, "dialogMessage");
        }
        else {
            dialogMessage = context.getString(dialogMessageId);
        }

        // Get the suffix for the number displayed in the dialog
        int suffixId = attrs.getAttributeResourceValue(ANDROID_SCHEMA_URL, "text", 0);
        if (suffixId == 0) {
            suffix = attrs.getAttributeValue(ANDROID_SCHEMA_URL, "text");
        }
        else {
            suffix = context.getString(suffixId);
        }

        // Get default, min, and max seekbar values
        defaultValue = attrs.getAttributeIntValue(ANDROID_SCHEMA_URL, "defaultValue", PreferenceConfiguration.getDefaultBitrate(context));
        maxValue = attrs.getAttributeIntValue(ANDROID_SCHEMA_URL, "max", 100);
        minValue = attrs.getAttributeIntValue(SEEKBAR_SCHEMA_URL, "min", 1);
        stepSize = attrs.getAttributeIntValue(SEEKBAR_SCHEMA_URL, "step", 1);
        divisor = attrs.getAttributeIntValue(SEEKBAR_SCHEMA_URL, "divisor", 1);
        keyStepSize = attrs.getAttributeIntValue(SEEKBAR_SCHEMA_URL, "keyStep", 0);
        currentValue = defaultValue;
    }

    private int committedValue() {
        if (!shouldPersist()) return currentValue;
        if (PreferenceConfiguration.BITRATE_PREF_STRING.equals(getKey())) {
            int stored = getPersistedInt(0);
            // Older installs and a first run can derive their bitrate from the active display.
            return stored > 0 ? stored : PreferenceConfiguration.readPreferences(context).bitrate;
        }
        return getPersistedInt(currentValue);
    }

    @Override
    protected View onCreateDialogView() {
        currentValue = committedValue();
        LinearLayout.LayoutParams params;
        LinearLayout layout = new LinearLayout(context);
        layout.setOrientation(LinearLayout.VERTICAL);
        boolean touch = com.limelight.utils.UiHelper.isTouchscreenClient(context);
        int inset = touch ? Math.round(22 * context.getResources().getDisplayMetrics().density) : 6;
        layout.setPadding(inset, touch ? inset / 2 : 6, inset, touch ? inset / 2 : 6);

        TextView splashText = new TextView(context);
        if (touch) {
            splashText.setTextSize(14);
            splashText.setTextColor(context.getColor(com.limelight.R.color.piulink_text_secondary));
        } else splashText.setPadding(30, 10, 30, 10);
        if (dialogMessage != null) {
            splashText.setText(dialogMessage);
        }
        layout.addView(splashText);

        valueText = new TextView(context);
        valueText.setGravity(touch ? Gravity.START : Gravity.CENTER_HORIZONTAL);
        valueText.setTextSize(touch ? 36 : 32);
        if (touch) valueText.setPadding(0, inset / 2, 0, inset / 3);
        valueText.setText(formatted(currentValue));
        params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                LinearLayout.LayoutParams.WRAP_CONTENT);
        layout.addView(valueText, params);

        seekBar = new SeekBar(context);
        seekBar.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override
            public void onProgressChanged(SeekBar seekBar, int value, boolean b) {
                if (value < minValue) {
                    seekBar.setProgress(minValue);
                    return;
                }

                int roundedValue = ((value + (stepSize - 1))/stepSize)*stepSize;
                if (roundedValue != value) {
                    seekBar.setProgress(roundedValue);
                    return;
                }

                valueText.setText(formatted(roundedValue));
            }

            @Override
            public void onStartTrackingTouch(SeekBar seekBar) {}

            @Override
            public void onStopTrackingTouch(SeekBar seekBar) {}
        });

        layout.addView(seekBar, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        seekBar.setMax(maxValue);
        if (keyStepSize != 0) {
            seekBar.setKeyProgressIncrement(keyStepSize);
        }
        seekBar.setProgress(currentValue);

        return layout;
    }

    @Override
    protected void onBindDialogView(View v) {
        super.onBindDialogView(v);
        seekBar.setMax(maxValue);
        if (keyStepSize != 0) {
            seekBar.setKeyProgressIncrement(keyStepSize);
        }
        seekBar.setProgress(currentValue);
    }

    @Override
    protected void onSetInitialValue(boolean restore, Object defaultValue)
    {
        super.onSetInitialValue(restore, defaultValue);
        if (restore) {
            currentValue = getPersistedInt(this.defaultValue);
        }
        else {
            currentValue = defaultValue instanceof Integer ? (Integer) defaultValue : this.defaultValue;
        }
    }

    public void setProgress(int progress) {
        this.currentValue = progress;
        if (seekBar != null) {
            seekBar.setProgress(progress);
        }
    }
    public int getProgress() {
        return currentValue;
    }

    private String formatted(int value) {
        String number = divisor == 1 ? Integer.toString(value) : String.format((Locale) null, "%.1f", value / (float) divisor);
        return suffix == null ? number : number + (suffix.length() > 1 ? " " : "") + suffix;
    }

    @Override protected void onBindView(View row) {
        super.onBindView(row);
        MobilePreferenceSheet.value(row, formatted(committedValue()));
    }

    @Override
    public void showDialog(Bundle state) {
        super.showDialog(state);
        MobilePreferenceSheet.show((AlertDialog) getDialog());

        Button positiveButton = ((AlertDialog) getDialog()).getButton(AlertDialog.BUTTON_POSITIVE);
        positiveButton.setOnClickListener(new OnClickListener() {
            @Override
            public void onClick(View view) {
                int selected = seekBar.getProgress();
                if (callChangeListener(selected)) {
                    currentValue = selected;
                    persistInt(selected);
                    notifyChanged();
                }

                getDialog().dismiss();
            }
        });
    }
    @Override protected void onPrepareDialogBuilder(android.app.AlertDialog.Builder builder) {
        super.onPrepareDialogBuilder(builder);
        com.limelight.utils.TouchDialog.preferenceHeader(builder, this);
    }
}
