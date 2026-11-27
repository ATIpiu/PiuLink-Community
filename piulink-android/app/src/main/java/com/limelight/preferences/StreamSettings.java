package com.limelight.preferences;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.media.MediaCodecInfo;
import android.os.Build;
import android.os.Bundle;
import android.app.Activity;
import android.os.Handler;
import android.os.Vibrator;
import android.preference.CheckBoxPreference;
import android.preference.ListPreference;
import android.preference.Preference;
import android.preference.PreferenceCategory;
import android.preference.PreferenceFragment;
import android.preference.PreferenceManager;
import android.preference.PreferenceScreen;
import android.util.DisplayMetrics;
import android.util.Range;
import android.view.Display;
import android.view.DisplayCutout;
import android.view.LayoutInflater;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.HorizontalScrollView;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import com.limelight.LimeLog;
import com.limelight.PcView;
import com.limelight.R;
import com.limelight.KeyInterceptorService;
import com.limelight.binding.video.MediaCodecHelper;
import com.limelight.utils.Dialog;
import com.limelight.utils.UiHelper;

import java.lang.reflect.Method;
import java.util.Arrays;

public class StreamSettings extends Activity {
    private static final String STATE_SELECTED_SECTION = "selected_section";
    private static final String ARG_SELECTED_SECTION = "selected_section";

    private static final String[] SECTION_KEYS = {
            "category_basic_settings",
            "category_audio_settings",
            "category_gamepad_settings",
            "category_input_settings",
            "category_onscreen_controls",
            "category_host_settings",
            "category_ui_settings",
            "category_advanced_settings"
    };

    private static final int[] SECTION_TITLES = {
            R.string.category_basic_settings,
            R.string.category_audio_settings,
            R.string.category_gamepad_settings,
            R.string.category_input_settings,
            R.string.category_on_screen_controls_settings,
            R.string.category_host_settings,
            R.string.category_ui_settings,
            R.string.category_advanced_settings
    };

    private PreferenceConfiguration previousPrefs;
    private int selectedSection = -1;
    private LinearLayout sectionContainer;
    private TextView sectionTitle;
    private View sectionScroller;
    private View backButton;
    private ListView settingsList;

    // HACK for Android 9
    static DisplayCutout displayCutoutP;

    void reloadSettings() {
        if (selectedSection < 0) return;
        SettingsFragment fragment = new SettingsFragment();
        Bundle arguments = new Bundle();
        arguments.putString(ARG_SELECTED_SECTION, SECTION_KEYS[selectedSection]);
        fragment.setArguments(arguments);
        getFragmentManager().beginTransaction()
                .replace(R.id.stream_settings, fragment)
                .commitAllowingStateLoss();
        getFragmentManager().executePendingTransactions();
        configureSettingsList(fragment);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        previousPrefs = PreferenceConfiguration.readPreferences(this);

        UiHelper.setLocale(this);

        if (savedInstanceState != null) {
            selectedSection = savedInstanceState.getInt(STATE_SELECTED_SECTION, -1);
            if (selectedSection < -1 || selectedSection >= SECTION_KEYS.length) selectedSection = -1;
        }

        bindSettingsLayout();
        UiHelper.notifyNewRootView(this);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::onBackPressed);
        }
    }

    private void bindSettingsLayout() {
        setContentView(R.layout.activity_stream_settings);
        sectionContainer = findViewById(R.id.settings_sections);
        sectionTitle = findViewById(R.id.settings_section_title);
        sectionScroller = findViewById(R.id.settings_tab_scroller);
        backButton = findViewById(R.id.settings_back);
        backButton.setOnClickListener(view -> onBackPressed());
        backButton.setOnKeyListener((view, keyCode, event) -> {
            if (keyCode == KeyEvent.KEYCODE_DPAD_DOWN &&
                    event.getAction() == KeyEvent.ACTION_DOWN && settingsList != null) {
                settingsList.requestFocus();
                if (settingsList.getSelectedItemPosition() < 0) settingsList.setSelection(0);
                return true;
            }
            return false;
        });
        populateSections();
        updateSelectedSection();
    }

    private void populateSections() {
        sectionContainer.removeAllViews();
        boolean vertical = true;
        boolean hasTouchscreen = getPackageManager().hasSystemFeature(PackageManager.FEATURE_TOUCHSCREEN);
        int gap = dp(8);

        TextView introduction = new TextView(this);
        introduction.setText(R.string.settings_overview_hint);
        introduction.setTextSize(14);
        introduction.setTextColor(getResources().getColor(R.color.piulink_text_secondary));
        introduction.setPadding(dp(8), 0, dp(8), dp(20));
        sectionContainer.addView(introduction);
        addUtility(R.string.setup_guide_title, () -> startActivity(
                new Intent(this, com.limelight.piulink.SetupActivity.class)));
        addUtility(R.string.open_tailscale, () ->
                com.limelight.utils.TailscaleLauncher.openAppOrPlayStore(this));

        for (int i = 0; i < SECTION_KEYS.length; i++) {
            if (!hasTouchscreen && "category_onscreen_controls".equals(SECTION_KEYS[i])) {
                continue;
            }

            final int section = i;
            TextView item = new TextView(this);
            item.setText(getString(SECTION_TITLES[i]).trim() + "  ›");
            item.setTextColor(getResources().getColorStateList(R.color.settings_section_text));
            item.setTextSize(14);
            item.setGravity(vertical ? android.view.Gravity.CENTER_VERTICAL : android.view.Gravity.CENTER);
            item.setBackgroundResource(R.drawable.bg_settings_section);
            item.setClickable(true);
            item.setFocusable(true);
            item.setMinHeight(dp(64));
            item.setPadding(dp(16), dp(10), dp(16), dp(10));
            item.setOnClickListener(view -> selectSection(section));

            LinearLayout.LayoutParams params;
            if (vertical) {
                params = new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT,
                        LinearLayout.LayoutParams.WRAP_CONTENT);
                params.bottomMargin = gap;
            }
            else {
                params = new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.WRAP_CONTENT,
                        LinearLayout.LayoutParams.WRAP_CONTENT);
                params.rightMargin = gap;
            }
            item.setLayoutParams(params);
            item.setTag(section);
            sectionContainer.addView(item);
        }
    }

    private void selectSection(int section) {
        if (selectedSection == section) {
            return;
        }
        selectedSection = section;
        updateSelectedSection();
        reloadSettings();
    }

    private void addUtility(int title, Runnable action) {
        TextView item = new TextView(this);
        item.setText(getString(title) + "  ›");
        item.setTextSize(16);
        item.setTextColor(getResources().getColor(R.color.piulink_blue));
        item.setPadding(dp(16), dp(18), dp(16), dp(18));
        item.setBackgroundResource(R.drawable.bg_settings_section);
        item.setFocusable(true);
        item.setOnClickListener(v -> action.run());
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.bottomMargin = dp(8);
        sectionContainer.addView(item, params);
    }

    private void updateSelectedSection() {
        boolean overview = selectedSection < 0;
        sectionTitle.setText(overview ? getString(R.string.settings_label)
                : getString(SECTION_TITLES[selectedSection]).trim());
        sectionScroller.setVisibility(overview ? View.VISIBLE : View.GONE);
        findViewById(R.id.settings_panel).setVisibility(overview ? View.GONE : View.VISIBLE);
        findViewById(R.id.settings_back).setVisibility(overview ? View.GONE : View.VISIBLE);
        for (int i = 0; i < sectionContainer.getChildCount(); i++) {
            View child = sectionContainer.getChildAt(i);
            if (!(child.getTag() instanceof Integer)) continue;
            boolean selected = ((Integer) child.getTag()) == selectedSection;
            child.setActivated(selected);
            if (selected) {
                child.post(() -> revealSelectedSection(child));
            }
        }
    }

    private void revealSelectedSection(View child) {
        if (sectionScroller instanceof HorizontalScrollView) {
            ((HorizontalScrollView) sectionScroller).smoothScrollTo(
                    Math.max(0, child.getLeft() - dp(12)), 0);
        }
        else if (sectionScroller instanceof ScrollView) {
            ((ScrollView) sectionScroller).smoothScrollTo(
                    0, Math.max(0, child.getTop() - dp(8)));
        }
    }

    private void configureSettingsList(SettingsFragment fragment) {
        if (fragment.getView() == null) return;
        settingsList = fragment.getView().findViewById(android.R.id.list);
        if (settingsList == null) return;
        boolean touch = UiHelper.isTouchscreenClient(this);
        settingsList.setSelector(touch ? android.R.color.transparent : R.drawable.bg_settings_list_focus);
        settingsList.setDrawSelectorOnTop(!touch);
        if (touch) {
            settingsList.setDivider(null);
            settingsList.setItemsCanFocus(true);
        }
        settingsList.setOnKeyListener((view, keyCode, event) -> {
            if (keyCode == KeyEvent.KEYCODE_DPAD_UP &&
                    event.getAction() == KeyEvent.ACTION_DOWN && isListAtTop(settingsList)) {
                backButton.requestFocus();
                return true;
            }
            return false;
        });
        settingsList.post(() -> {
            settingsList.requestFocus();
            if (settingsList.getSelectedItemPosition() < 0) settingsList.setSelection(0);
        });
    }

    private static boolean isListAtTop(ListView list) {
        int selected = list.getSelectedItemPosition();
        if (selected > 0) return false;
        if (list.getFirstVisiblePosition() > 0) return false;
        View first = list.getChildAt(0);
        return first == null || first.getTop() >= list.getPaddingTop();
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    @Override
    protected void onSaveInstanceState(Bundle outState) {
        outState.putInt(STATE_SELECTED_SECTION, selectedSection);
        super.onSaveInstanceState(outState);
    }

    @Override
    public void onAttachedToWindow() {
        super.onAttachedToWindow();

        // We have to use this hack on Android 9 because we don't have Display.getCutout()
        // which was added in Android 10.
        if (Build.VERSION.SDK_INT == Build.VERSION_CODES.P) {
            // Insets can be null when the activity is recreated on screen rotation
            // https://stackoverflow.com/questions/61241255/windowinsets-getdisplaycutout-is-null-everywhere-except-within-onattachedtowindo
            WindowInsets insets = getWindow().getDecorView().getRootWindowInsets();
            if (insets != null) {
                displayCutoutP = insets.getDisplayCutout();
            }
        }

        reloadSettings();
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);
        bindSettingsLayout();
        UiHelper.notifyNewRootView(this);
        reloadSettings();
    }

    @Override
    // NOTE: This will NOT be called on Android 13+ with android:enableOnBackInvokedCallback="true"
    public void onBackPressed() {
        if (selectedSection >= 0) {
            selectedSection = -1;
            updateSelectedSection();
            return;
        }
        finish();

        // Language changes are handled via configuration changes in Android 13+,
        // so manual activity relaunching is no longer required.
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU) {
            PreferenceConfiguration newPrefs = PreferenceConfiguration.readPreferences(this);
            if (!newPrefs.language.equals(previousPrefs.language)) {
                // Restart the PC view to apply UI changes
                Intent intent = new Intent(this, PcView.class);
                intent.setFlags(Intent.FLAG_ACTIVITY_CLEAR_TASK | Intent.FLAG_ACTIVITY_NEW_TASK);
                startActivity(intent, null);
            }
        }
    }

    public static class SettingsFragment extends PreferenceFragment {
        private int nativeResolutionStartIndex = Integer.MAX_VALUE;
        private boolean nativeFramerateShown = false;

        private void setValue(String preferenceKey, String value) {
            ListPreference pref = (ListPreference) findPreference(preferenceKey);

            pref.setValue(value);
        }

        private void appendPreferenceEntry(ListPreference pref, String newEntryName, String newEntryValue) {
            CharSequence[] newEntries = Arrays.copyOf(pref.getEntries(), pref.getEntries().length + 1);
            CharSequence[] newValues = Arrays.copyOf(pref.getEntryValues(), pref.getEntryValues().length + 1);

            // Add the new option
            newEntries[newEntries.length - 1] = newEntryName;
            newValues[newValues.length - 1] = newEntryValue;

            pref.setEntries(newEntries);
            pref.setEntryValues(newValues);
        }

        private void addNativeResolutionEntry(int nativeWidth, int nativeHeight, boolean insetsRemoved, boolean portrait) {
            ListPreference pref = (ListPreference) findPreference(PreferenceConfiguration.RESOLUTION_PREF_STRING);

            String newName;

            if (insetsRemoved) {
                newName = getResources().getString(R.string.resolution_prefix_native_fullscreen);
            }
            else {
                newName = getResources().getString(R.string.resolution_prefix_native);
            }

            if (PreferenceConfiguration.isSquarishScreen(nativeWidth, nativeHeight)) {
                if (portrait) {
                    newName += " " + getResources().getString(R.string.resolution_prefix_native_portrait);
                }
                else {
                    newName += " " + getResources().getString(R.string.resolution_prefix_native_landscape);
                }
            }

            newName += " ("+nativeWidth+"x"+nativeHeight+")";

            String newValue = nativeWidth+"x"+nativeHeight;

            // Check if the native resolution is already present
            for (CharSequence value : pref.getEntryValues()) {
                if (newValue.equals(value.toString())) {
                    // It is present in the default list, so don't add it again
                    return;
                }
            }

            if (pref.getEntryValues().length < nativeResolutionStartIndex) {
                nativeResolutionStartIndex = pref.getEntryValues().length;
            }
            appendPreferenceEntry(pref, newName, newValue);
        }

        private void addNativeResolutionEntries(int nativeWidth, int nativeHeight, boolean insetsRemoved) {
            if (PreferenceConfiguration.isSquarishScreen(nativeWidth, nativeHeight)) {
                addNativeResolutionEntry(nativeHeight, nativeWidth, insetsRemoved, true);
            }
            addNativeResolutionEntry(nativeWidth, nativeHeight, insetsRemoved, false);
        }

        private void addNativeFrameRateEntry(float framerate) {
            int frameRateRounded = Math.round(framerate);
            if (frameRateRounded == 0) {
                return;
            }

            ListPreference pref = (ListPreference) findPreference(PreferenceConfiguration.FPS_PREF_STRING);
            String fpsValue = Integer.toString(frameRateRounded);
            String fpsName = getResources().getString(R.string.resolution_prefix_native) +
                    " (" + fpsValue + " " + getResources().getString(R.string.fps_suffix_fps) + ")";

            // Check if the native frame rate is already present
            for (CharSequence value : pref.getEntryValues()) {
                if (fpsValue.equals(value.toString())) {
                    // It is present in the default list, so don't add it again
                    nativeFramerateShown = false;
                    return;
                }
            }

            appendPreferenceEntry(pref, fpsName, fpsValue);
            nativeFramerateShown = true;
        }

        private void removeValue(String preferenceKey, String value, Runnable onMatched) {
            int matchingCount = 0;

            ListPreference pref = (ListPreference) findPreference(preferenceKey);

            // Count the number of matching entries we'll be removing
            for (CharSequence seq : pref.getEntryValues()) {
                if (seq.toString().equalsIgnoreCase(value)) {
                    matchingCount++;
                }
            }

            // Create the new arrays
            CharSequence[] entries = new CharSequence[pref.getEntries().length-matchingCount];
            CharSequence[] entryValues = new CharSequence[pref.getEntryValues().length-matchingCount];
            int outIndex = 0;
            for (int i = 0; i < pref.getEntryValues().length; i++) {
                if (pref.getEntryValues()[i].toString().equalsIgnoreCase(value)) {
                    // Skip matching values
                    continue;
                }

                entries[outIndex] = pref.getEntries()[i];
                entryValues[outIndex] = pref.getEntryValues()[i];
                outIndex++;
            }

            if (pref.getValue().equalsIgnoreCase(value)) {
                onMatched.run();
            }

            // Update the preference with the new list
            pref.setEntries(entries);
            pref.setEntryValues(entryValues);
        }

        private void resetBitrateToDefault(SharedPreferences prefs, String res, String fps) {
            if (res == null) {
                res = prefs.getString(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.DEFAULT_RESOLUTION);
            }
            if (fps == null) {
                fps = prefs.getString(PreferenceConfiguration.FPS_PREF_STRING, PreferenceConfiguration.DEFAULT_FPS);
            }

            prefs.edit()
                    .putInt(PreferenceConfiguration.BITRATE_PREF_STRING,
                            PreferenceConfiguration.getDefaultBitrate(res, fps))
                    .apply();
        }

        @Override
        public View onCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState) {
            View view = super.onCreateView(inflater, container, savedInstanceState);
            view.setBackgroundColor(android.graphics.Color.TRANSPARENT);
            UiHelper.applyStatusBarPadding(view);
            return view;
        }

        @Override
        public void onCreate(Bundle savedInstanceState) {
            super.onCreate(savedInstanceState);

            addPreferencesFromResource(R.xml.preferences);
            PreferenceScreen screen = getPreferenceScreen();

            Preference reminders = findPreference("manage_reminders");
            if (reminders != null) {
                reminders.setOnPreferenceClickListener(preference -> {
                    com.limelight.utils.ReminderPreferences.manage(getActivity());
                    return true;
                });
            }

            Preference updates = findPreference("check_updates");
            if (updates != null) updates.setOnPreferenceClickListener(pref -> {
                startActivity(new Intent(getActivity(), UpdateActivity.class));
                return true;
            });
            Preference viewLogs = findPreference("view_logs");
            if (viewLogs != null) {
                viewLogs.setOnPreferenceClickListener(preference -> {
                    startActivity(new Intent(getActivity(), LogActivity.class));
                    return true;
                });
            }

            Preference inviteRoute = findPreference(com.limelight.utils.NetHelper.DIRECT_INVITE_NETWORK);
            if (inviteRoute != null) inviteRoute.setOnPreferenceChangeListener((preference, value) -> {
                com.limelight.piulink.InviteVpnChoice.noteChoice(preference.getContext());
                return true;
            });
            CheckBoxPreference autoBitratePref =
                    (CheckBoxPreference) findPreference(PreferenceConfiguration.AUTO_BITRATE_PREF_STRING);
            Preference manualBitratePref =
                    findPreference(PreferenceConfiguration.BITRATE_PREF_STRING);
            Preference saveMeteredDataPref =
                    findPreference(PreferenceConfiguration.SAVE_METERED_DATA_PREF_STRING);
            Preference autoBitrateModePref =
                    findPreference(PreferenceConfiguration.AUTO_BITRATE_MODE_PREF_STRING);
            if (autoBitratePref != null && manualBitratePref != null &&
                    saveMeteredDataPref != null && autoBitrateModePref != null) {
                manualBitratePref.setEnabled(!autoBitratePref.isChecked());
                saveMeteredDataPref.setEnabled(autoBitratePref.isChecked());
                autoBitrateModePref.setEnabled(autoBitratePref.isChecked());
                autoBitratePref.setOnPreferenceChangeListener((preference, newValue) -> {
                    manualBitratePref.setEnabled(!(Boolean) newValue);
                    saveMeteredDataPref.setEnabled((Boolean) newValue);
                    autoBitrateModePref.setEnabled((Boolean) newValue);
                    return true;
                });
            }

            CheckBoxPreference keyboardInterceptorPref = (CheckBoxPreference) findPreference("checkbox_keyboard_interceptor");
            if (keyboardInterceptorPref != null) {
                keyboardInterceptorPref.setOnPreferenceChangeListener(new Preference.OnPreferenceChangeListener() {
                    @Override
                    public boolean onPreferenceChange(Preference preference, Object newValue) {
                        boolean enabled = (Boolean) newValue;
                        if (!enabled) {
                            return true;
                        }

                        Activity activity = getActivity();
                        if (AccessibilityDisclosureController.hasRecordedConsent(activity) &&
                                isAccessibilityServiceEnabled(activity)) {
                            return true;
                        }

                        // Keep the preference disabled until the user affirmatively accepts the
                        // dedicated disclosure. Opening Android settings alone is not consent.
                        AccessibilityDisclosureController.showIfNeeded(activity,
                                new AccessibilityDisclosureController.Callback() {
                                    @Override
                                    public void onAccepted() {
                                        keyboardInterceptorPref.setChecked(true);
                                        if (!isAccessibilityServiceEnabled(activity)) {
                                            openAccessibilitySettings();
                                        }
                                    }

                                    @Override
                                    public void onDeclined() {
                                        keyboardInterceptorPref.setChecked(false);
                                    }
                                });
                        return false;
                    }
                });
            }

            // hide on-screen controls category on non touch screen devices
            if (!getActivity().getPackageManager().hasSystemFeature(PackageManager.FEATURE_TOUCHSCREEN)) {
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_onscreen_controls");
                screen.removePreference(category);
            }

            // Hide remote desktop mouse mode on pre-Oreo (which doesn't have pointer capture)
            // and NVIDIA SHIELD devices (which support raw mouse input in pointer capture mode)
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
                    getActivity().getPackageManager().hasSystemFeature("com.nvidia.feature.shield")) {
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_input_settings");
                category.removePreference(findPreference("checkbox_absolute_mouse_mode"));
            }

            // Hide gamepad motion sensor option when running on OSes before Android 12.
            // Support for motion, LED, battery, and other extensions were introduced in S.
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_gamepad_settings");
                category.removePreference(findPreference("checkbox_gamepad_motion_sensors"));
            }

            // Hide gamepad motion sensor fallback option if the device has no gyro or accelerometer
            if (!getActivity().getPackageManager().hasSystemFeature(PackageManager.FEATURE_SENSOR_ACCELEROMETER) &&
                    !getActivity().getPackageManager().hasSystemFeature(PackageManager.FEATURE_SENSOR_GYROSCOPE)) {
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_gamepad_settings");
                category.removePreference(findPreference("checkbox_gamepad_motion_fallback"));
            }

            // Hide USB driver options on devices without USB host support
            if (!getActivity().getPackageManager().hasSystemFeature(PackageManager.FEATURE_USB_HOST)) {
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_gamepad_settings");
                category.removePreference(findPreference("checkbox_usb_bind_all"));
                category.removePreference(findPreference("checkbox_usb_driver"));
            }

            // Fire TV apps are not allowed to use WebViews or browsers, so hide the Help category
            /*if (getActivity().getPackageManager().hasSystemFeature("amazon.hardware.fire_tv")) {
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_help");
                screen.removePreference(category);
            }*/
            PreferenceCategory category_gamepad_settings =
                    (PreferenceCategory) findPreference("category_gamepad_settings");
            // Remove the vibration options if the device can't vibrate
            if (!((Vibrator)getActivity().getSystemService(Context.VIBRATOR_SERVICE)).hasVibrator()) {
                category_gamepad_settings.removePreference(findPreference("checkbox_vibrate_fallback"));
                category_gamepad_settings.removePreference(findPreference("seekbar_vibrate_fallback_strength"));
                // The entire OSC category may have already been removed by the touchscreen check above
                PreferenceCategory category = (PreferenceCategory) findPreference("category_onscreen_controls");
                if (category != null) {
                    category.removePreference(findPreference("checkbox_vibrate_osc"));
                }
            }
            else if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
                    !((Vibrator)getActivity().getSystemService(Context.VIBRATOR_SERVICE)).hasAmplitudeControl() ) {
                // Remove the vibration strength selector of the device doesn't have amplitude control
                category_gamepad_settings.removePreference(findPreference("seekbar_vibrate_fallback_strength"));
            }

            Display display = getActivity().getWindowManager().getDefaultDisplay();
            float maxSupportedFps = display.getRefreshRate();

            // Hide non-supported resolution/FPS combinations
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                int maxSupportedResW = 0;

                // Add a native resolution with any insets included for users that don't want content
                // behind the notch of their display
                boolean hasInsets = false;
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                    DisplayCutout cutout;

                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        // Use the much nicer Display.getCutout() API on Android 10+
                        cutout = display.getCutout();
                    }
                    else {
                        // Android 9 only
                        cutout = displayCutoutP;
                    }

                    if (cutout != null) {
                        int widthInsets = cutout.getSafeInsetLeft() + cutout.getSafeInsetRight();
                        int heightInsets = cutout.getSafeInsetBottom() + cutout.getSafeInsetTop();

                        if (widthInsets != 0 || heightInsets != 0) {
                            DisplayMetrics metrics = new DisplayMetrics();
                            display.getRealMetrics(metrics);

                            int width = Math.max(metrics.widthPixels - widthInsets, metrics.heightPixels - heightInsets);
                            int height = Math.min(metrics.widthPixels - widthInsets, metrics.heightPixels - heightInsets);

                            addNativeResolutionEntries(width, height, false);
                            hasInsets = true;
                        }
                    }
                }

                // Always allow resolutions that are smaller or equal to the active
                // display resolution because decoders can report total non-sense to us.
                // For example, a p201 device reports:
                // AVC Decoder: OMX.amlogic.avc.decoder.awesome
                // HEVC Decoder: OMX.amlogic.hevc.decoder.awesome
                // AVC supported width range: 64 - 384
                // HEVC supported width range: 64 - 544
                for (Display.Mode candidate : display.getSupportedModes()) {
                    // Some devices report their dimensions in the portrait orientation
                    // where height > width. Normalize these to the conventional width > height
                    // arrangement before we process them.

                    int width = Math.max(candidate.getPhysicalWidth(), candidate.getPhysicalHeight());
                    int height = Math.min(candidate.getPhysicalWidth(), candidate.getPhysicalHeight());

                    // Some TVs report strange values here, so let's avoid native resolutions on a TV
                    // unless they report greater than 4K resolutions.
                    if (!getActivity().getPackageManager().hasSystemFeature(PackageManager.FEATURE_TELEVISION) ||
                            (width > 3840 || height > 2160)) {
                        addNativeResolutionEntries(width, height, hasInsets);
                    }

                    if ((width >= 3840 || height >= 2160) && maxSupportedResW < 3840) {
                        maxSupportedResW = 3840;
                    }
                    else if ((width >= 2560 || height >= 1440) && maxSupportedResW < 2560) {
                        maxSupportedResW = 2560;
                    }
                    else if ((width >= 1920 || height >= 1080) && maxSupportedResW < 1920) {
                        maxSupportedResW = 1920;
                    }

                    if (candidate.getRefreshRate() > maxSupportedFps) {
                        maxSupportedFps = candidate.getRefreshRate();
                    }
                }

                // This must be called to do runtime initialization before calling functions that evaluate
                // decoder lists.
                MediaCodecHelper.initialize(getContext(), GlPreferences.readPreferences(getContext()).glRenderer);

                MediaCodecInfo avcDecoder = MediaCodecHelper.findProbableSafeDecoder("video/avc", -1);
                MediaCodecInfo hevcDecoder = MediaCodecHelper.findProbableSafeDecoder("video/hevc", -1);

                if (avcDecoder != null) {
                    Range<Integer> avcWidthRange = avcDecoder.getCapabilitiesForType("video/avc").getVideoCapabilities().getSupportedWidths();

                    LimeLog.info("AVC supported width range: "+avcWidthRange.getLower()+" - "+avcWidthRange.getUpper());

                    // If 720p is not reported as supported, ignore all results from this API
                    if (avcWidthRange.contains(1280)) {
                        if (avcWidthRange.contains(3840) && maxSupportedResW < 3840) {
                            maxSupportedResW = 3840;
                        }
                        else if (avcWidthRange.contains(1920) && maxSupportedResW < 1920) {
                            maxSupportedResW = 1920;
                        }
                        else if (maxSupportedResW < 1280) {
                            maxSupportedResW = 1280;
                        }
                    }
                }

                if (hevcDecoder != null) {
                    Range<Integer> hevcWidthRange = hevcDecoder.getCapabilitiesForType("video/hevc").getVideoCapabilities().getSupportedWidths();

                    LimeLog.info("HEVC supported width range: "+hevcWidthRange.getLower()+" - "+hevcWidthRange.getUpper());

                    // If 720p is not reported as supported, ignore all results from this API
                    if (hevcWidthRange.contains(1280)) {
                        if (hevcWidthRange.contains(3840) && maxSupportedResW < 3840) {
                            maxSupportedResW = 3840;
                        }
                        else if (hevcWidthRange.contains(1920) && maxSupportedResW < 1920) {
                            maxSupportedResW = 1920;
                        }
                        else if (maxSupportedResW < 1280) {
                            maxSupportedResW = 1280;
                        }
                    }
                }

                LimeLog.info("Maximum resolution slot: "+maxSupportedResW);

                if (maxSupportedResW != 0) {
                    if (maxSupportedResW < 3840) {
                        // 4K is unsupported
                        removeValue(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.RES_4K, new Runnable() {
                            @Override
                            public void run() {
                                SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                                setValue(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.RES_1440P);
                                resetBitrateToDefault(prefs, null, null);
                            }
                        });
                    }
                    if (maxSupportedResW < 2560) {
                        // 1440p is unsupported
                        removeValue(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.RES_1440P, new Runnable() {
                            @Override
                            public void run() {
                                SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                                setValue(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.RES_1080P);
                                resetBitrateToDefault(prefs, null, null);
                            }
                        });
                    }
                    if (maxSupportedResW < 1920) {
                        // 1080p is unsupported
                        removeValue(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.RES_1080P, new Runnable() {
                            @Override
                            public void run() {
                                SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                                setValue(PreferenceConfiguration.RESOLUTION_PREF_STRING, PreferenceConfiguration.RES_720P);
                                resetBitrateToDefault(prefs, null, null);
                            }
                        });
                    }
                    // Never remove 720p
                }
            }
            else {
                // We can get the true metrics via the getRealMetrics() function (unlike the lies
                // that getWidth() and getHeight() tell to us).
                DisplayMetrics metrics = new DisplayMetrics();
                display.getRealMetrics(metrics);
                int width = Math.max(metrics.widthPixels, metrics.heightPixels);
                int height = Math.min(metrics.widthPixels, metrics.heightPixels);
                addNativeResolutionEntries(width, height, false);
            }

            if (!PreferenceConfiguration.readPreferences(this.getActivity()).unlockFps) {
                // We give some extra room in case the FPS is rounded down
                if (maxSupportedFps < 118) {
                    removeValue(PreferenceConfiguration.FPS_PREF_STRING, "120", new Runnable() {
                        @Override
                        public void run() {
                            SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                            setValue(PreferenceConfiguration.FPS_PREF_STRING, "90");
                            resetBitrateToDefault(prefs, null, null);
                        }
                    });
                }
                if (maxSupportedFps < 88) {
                    // 1080p is unsupported
                    removeValue(PreferenceConfiguration.FPS_PREF_STRING, "90", new Runnable() {
                        @Override
                        public void run() {
                            SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                            setValue(PreferenceConfiguration.FPS_PREF_STRING, "60");
                            resetBitrateToDefault(prefs, null, null);
                        }
                    });
                }
                // Never remove 30 FPS or 60 FPS
            }
            addNativeFrameRateEntry(maxSupportedFps);
            // Host-advertised modes (for example 1366x768 or 165 Hz) may not be
            // in the static phone list. Keep the saved selection visible.
            for (String modeKey : new String[]{PreferenceConfiguration.RESOLUTION_PREF_STRING,
                    PreferenceConfiguration.FPS_PREF_STRING}) {
                ListPreference modePreference = (ListPreference) findPreference(modeKey);
                String value = modePreference.getValue();
                if (value != null && modePreference.findIndexOfValue(value) < 0) {
                    appendPreferenceEntry(modePreference,
                            modeKey.equals(PreferenceConfiguration.FPS_PREF_STRING)
                                    ? value + " " + getString(R.string.fps_suffix_fps) : value, value);
                }
            }

            // Android L introduces the drop duplicate behavior of releaseOutputBuffer()
            // that the unlock FPS option relies on to not massively increase latency.
            findPreference(PreferenceConfiguration.UNLOCK_FPS_STRING).setOnPreferenceChangeListener(new Preference.OnPreferenceChangeListener() {
                @Override
                public boolean onPreferenceChange(Preference preference, Object newValue) {
                    // HACK: We need to let the preference change succeed before reinitializing to ensure
                    // it's reflected in the new layout.
                    final Handler h = new Handler();
                    h.postDelayed(new Runnable() {
                        @Override
                        public void run() {
                            // Ensure the activity is still open when this timeout expires
                            Activity settingsActivity = SettingsFragment.this.getActivity();
                            if (settingsActivity instanceof StreamSettings) {
                                ((StreamSettings) settingsActivity).reloadSettings();
                            } else if (settingsActivity instanceof PcView) {
                                ((PcView) settingsActivity).reloadHomeSettings();
                            }
                        }
                    }, 500);

                    // Allow the original preference change to take place
                    return true;
                }
            });

            // Remove HDR preference for devices below Nougat
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.N) {
                LimeLog.info("Excluding HDR toggle based on OS");
                PreferenceCategory category =
                        (PreferenceCategory) findPreference("category_advanced_settings");
                category.removePreference(findPreference("checkbox_enable_hdr"));
            }
            else {
                Display.HdrCapabilities hdrCaps = display.getHdrCapabilities();

                // We must now ensure our display is compatible with HDR10
                boolean foundHdr10 = false;
                if (hdrCaps != null) {
                    // getHdrCapabilities() returns null on Lenovo Lenovo Mirage Solo (vega), Android 8.0
                    for (int hdrType : hdrCaps.getSupportedHdrTypes()) {
                        if (hdrType == Display.HdrCapabilities.HDR_TYPE_HDR10) {
                            foundHdr10 = true;
                            break;
                        }
                    }
                }

                if (!foundHdr10) {
                    LimeLog.info("Excluding HDR toggle based on display capabilities");
                    PreferenceCategory category =
                            (PreferenceCategory) findPreference("category_advanced_settings");
                    category.removePreference(findPreference("checkbox_enable_hdr"));
                }
                else if (PreferenceConfiguration.isShieldAtvFirmwareWithBrokenHdr()) {
                    LimeLog.info("Disabling HDR toggle on old broken SHIELD TV firmware");
                    PreferenceCategory category =
                            (PreferenceCategory) findPreference("category_advanced_settings");
                    CheckBoxPreference hdrPref = (CheckBoxPreference) category.findPreference("checkbox_enable_hdr");
                    hdrPref.setEnabled(false);
                    hdrPref.setChecked(false);
                    hdrPref.setSummary("Update the firmware on your NVIDIA SHIELD Android TV to enable HDR");
                }
            }

            // Make the precedence explicit: automatic matching owns resolution, while FPS
            // remains a default that can still be overridden by per-host Display settings.
            CheckBoxPreference smartDisplayPreference =
                    (CheckBoxPreference) findPreference("checkbox_smart_display");
            Preference manualResolutionPreference =
                    findPreference(PreferenceConfiguration.RESOLUTION_PREF_STRING);
            manualResolutionPreference.setEnabled(!smartDisplayPreference.isChecked());
            smartDisplayPreference.setOnPreferenceChangeListener((preference, newValue) -> {
                manualResolutionPreference.setEnabled(!(Boolean) newValue);
                return true;
            });

            // Add a listener to the FPS and resolution preference
            // so the bitrate can be auto-adjusted
            findPreference(PreferenceConfiguration.RESOLUTION_PREF_STRING).setOnPreferenceChangeListener(new Preference.OnPreferenceChangeListener() {
                @Override
                public boolean onPreferenceChange(Preference preference, Object newValue) {
                    SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                    String valueStr = (String) newValue;

                    // Detect if this value is the native resolution option
                    CharSequence[] values = ((ListPreference)preference).getEntryValues();
                    boolean isNativeRes = true;
                    for (int i = 0; i < values.length; i++) {
                        // Look for a match prior to the start of the native resolution entries
                        if (valueStr.equals(values[i].toString()) && i < nativeResolutionStartIndex) {
                            isNativeRes = false;
                            break;
                        }
                    }

                    // If this is native resolution, show the warning dialog
                    if (isNativeRes) {
                        Dialog.displayTip(getActivity(), R.string.title_native_res_dialog,
                                R.string.text_native_res_dialog, () -> { });
                    }

                    // Write the new bitrate value
                    resetBitrateToDefault(prefs, valueStr, null);

                    // Allow the original preference change to take place
                    return true;
                }
            });
            findPreference(PreferenceConfiguration.FPS_PREF_STRING).setOnPreferenceChangeListener(new Preference.OnPreferenceChangeListener() {
                @Override
                public boolean onPreferenceChange(Preference preference, Object newValue) {
                    SharedPreferences prefs = PreferenceManager.getDefaultSharedPreferences(SettingsFragment.this.getActivity());
                    String valueStr = (String) newValue;

                    // If this is native frame rate, show the warning dialog
                    CharSequence[] values = ((ListPreference)preference).getEntryValues();
                    if (nativeFramerateShown && values[values.length - 1].toString().equals(newValue.toString())) {
                        Dialog.displayTip(getActivity(), R.string.title_native_fps_dialog,
                                R.string.text_native_res_dialog, () -> { });
                    }

                    // Write the new bitrate value
                    resetBitrateToDefault(prefs, null, valueStr);

                    // Allow the original preference change to take place
                    return true;
                }
            });

            // This screen uses section navigation, so keep only the selected group in the list.
            String selectedSection = getArguments() != null
                    ? getArguments().getString(ARG_SELECTED_SECTION)
                    : SECTION_KEYS[0];
            for (String sectionKey : SECTION_KEYS) {
                if (!sectionKey.equals(selectedSection)) {
                    Preference category = findPreference(sectionKey);
                    if (category != null) {
                        screen.removePreference(category);
                    }
                }
            }
            // The surrounding page already names this category. Put its settings directly
            // in the list and lead each choice with its current value.
            Preference selectedGroup = findPreference(selectedSection);
            if (selectedGroup instanceof PreferenceCategory) {
                PreferenceCategory group = (PreferenceCategory) selectedGroup;
                if ("category_basic_settings".equals(selectedSection)) {
                    // Display target, resolution, and frame rate now have one authoritative
                    // per-PC entry in the in-stream Display settings panel.
                    Preference smartDisplay = findPreference("checkbox_smart_display");
                    Preference resolution = findPreference(PreferenceConfiguration.RESOLUTION_PREF_STRING);
                    Preference frameRate = findPreference(PreferenceConfiguration.FPS_PREF_STRING);
                    if (smartDisplay != null) group.removePreference(smartDisplay);
                    if (resolution != null) group.removePreference(resolution);
                    if (frameRate != null) group.removePreference(frameRate);
                }
                else if ("category_advanced_settings".equals(selectedSection)) {
                    // The unified Display settings dialog now lists only frame rates the
                    // Android display can present, so a separate unlock switch is obsolete.
                    Preference unlockFps = findPreference(PreferenceConfiguration.UNLOCK_FPS_STRING);
                    if (unlockFps != null) group.removePreference(unlockFps);
                }
                screen.removePreference(group);
                com.limelight.piulink.MobileSettingsSection mobile = com.limelight.piulink.MobileSettingsSection.forKey(selectedSection);
                if (UiHelper.isTouchscreenClient(getActivity()) && mobile != null) {
                    mobile.populate(screen, group);
                    return;
                }
                while (group.getPreferenceCount() > 0) {
                    Preference child = group.getPreference(0);
                    group.removePreference(child);
                    screen.addPreference(child);
                }
            }
            showCurrentValues(screen);
        }

        private void showCurrentValues(android.preference.PreferenceGroup group) {
            for (int i = 0; i < group.getPreferenceCount(); i++) {
                Preference item = group.getPreference(i);
                if (item instanceof ListPreference) {
                    CharSequence description = item.getSummary();
                    item.setSummary("%s" + (description == null ? "" : "\n" + description.toString().replace("%", "%%")));
                } else if (item instanceof android.preference.PreferenceGroup) {
                    showCurrentValues((android.preference.PreferenceGroup) item);
                }
            }
        }

        private boolean isAccessibilityServiceEnabled(Context context) {
            String service = context.getPackageName() + "/" + KeyInterceptorService.class.getName();
            int accessibilityEnabled = 0;
            try {
                accessibilityEnabled = android.provider.Settings.Secure.getInt(
                        context.getContentResolver(),
                        android.provider.Settings.Secure.ACCESSIBILITY_ENABLED);
            } catch (android.provider.Settings.SettingNotFoundException e) {
                // Ignored
            }

            android.text.TextUtils.SimpleStringSplitter colonSplitter = new android.text.TextUtils.SimpleStringSplitter(':');

            if (accessibilityEnabled == 1) {
                String settingValue = android.provider.Settings.Secure.getString(
                        context.getContentResolver(),
                        android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES);
                if (settingValue != null) {
                    colonSplitter.setString(settingValue);
                    while (colonSplitter.hasNext()) {
                        String accessibilityService = colonSplitter.next();
                        if (accessibilityService.equalsIgnoreCase(service)) {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void openAccessibilitySettings() {
            try {
                Intent intent = new Intent(android.provider.Settings.ACTION_ACCESSIBILITY_SETTINGS);
                startActivity(intent);
            } catch (Exception e) {
                android.widget.Toast.makeText(getActivity(),
                        R.string.accessibility_settings_unavailable,
                        android.widget.Toast.LENGTH_SHORT).show();
            }
        }
    }
}
