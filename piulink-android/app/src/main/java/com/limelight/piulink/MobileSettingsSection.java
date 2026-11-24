package com.limelight.piulink;

import android.content.Context;
import android.preference.CheckBoxPreference;
import android.preference.ListPreference;
import android.preference.Preference;
import android.preference.PreferenceCategory;
import android.preference.PreferenceScreen;

import com.limelight.R;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Presentation of the existing, device-filtered preferences on touch clients. */
public enum MobileSettingsSection {
    PICTURE("category_basic_settings", "checkbox_auto_bitrate", R.string.settings_quality, R.string.detail_rate, R.drawable.detail_monitor,
            true, R.string.detail_auto, R.string.detail_manual, R.string.detail_rate_auto_hint, R.string.detail_rate_fixed_hint),
    SOUND("category_audio_settings", "list_audio_config", R.string.settings_sound, R.string.detail_channels, R.drawable.detail_headphones),
    GAMEPAD("category_gamepad_settings", "checkbox_multi_controller", R.string.settings_hardware_pad, R.string.detail_pad_count, R.drawable.detail_gamepad,
            true, R.string.detail_separate_pads, R.string.detail_one_pad, R.string.detail_multi_on_hint, R.string.detail_multi_off_hint),
    TOUCH("category_input_settings", "checkbox_touchscreen_trackpad", R.string.settings_touch, R.string.detail_pointer, R.drawable.detail_trackpad,
            true, R.string.detail_trackpad, R.string.detail_direct, R.string.detail_touch_trackpad_hint, R.string.detail_touch_direct_hint),
    OVERLAY("category_onscreen_controls", "checkbox_show_onscreen_controls", R.string.settings_overlay_pad, R.string.detail_virtual_pad, R.drawable.detail_gamepad,
            true, R.string.detail_on, R.string.detail_off, R.string.detail_osc_on_hint, R.string.detail_osc_off_hint),
    HOST("category_host_settings", "checkbox_host_audio", R.string.settings_host_label, R.string.detail_playback, R.drawable.detail_monitor,
            false, R.string.detail_both_devices, R.string.detail_client_only, R.string.detail_host_on_hint, R.string.detail_host_off_hint),
    APPEARANCE("category_ui_settings", "checkbox_enable_pip", R.string.settings_appearance, R.string.detail_pip, R.drawable.detail_monitor,
            true, R.string.detail_on, R.string.detail_off, R.string.detail_pip_on_hint, R.string.detail_pip_off_hint),
    ADVANCED("category_advanced_settings", "video_format", R.string.settings_advanced_label, R.string.detail_codec, R.drawable.detail_monitor);

    public final String key;
    final String feature;
    public final int title;
    final int field, art;
    final boolean firstOn;
    final int onLabel, offLabel, onHelp, offHelp;

    MobileSettingsSection(String key, String feature, int title, int field, int art) {
        this(key, feature, title, field, art, false, 0, 0, 0, 0);
    }

    MobileSettingsSection(String key, String feature, int title, int field, int art, boolean firstOn,
                          int onLabel, int offLabel, int onHelp, int offHelp) {
        this.key = key;
        this.feature = feature;
        this.title = title;
        this.field = field;
        this.art = art;
        this.firstOn = firstOn;
        this.onLabel = onLabel;
        this.offLabel = offLabel;
        this.onHelp = onHelp;
        this.offHelp = offHelp;
    }

    public static MobileSettingsSection forKey(String key) {
        for (MobileSettingsSection section : values()) if (section.key.equals(key)) return section;
        return null;
    }

    static MobileSettingsSection forFeature(String key) {
        for (MobileSettingsSection section : values()) if (section.feature.equals(key)) return section;
        return null;
    }

    public void populate(PreferenceScreen screen, PreferenceCategory original) {
        Context context = screen.getContext();
        Map<Integer, List<Preference>> groups = new LinkedHashMap<>();
        while (original.getPreferenceCount() > 0) {
            Preference child = original.getPreference(0);
            original.removePreference(child);
            if (feature.equals(child.getKey())) {
                child.setOrder(0);
                child.setLayoutResource(R.layout.detail_feature_preference);
                child.setWidgetLayoutResource(0);
                screen.addPreference(child);
            } else {
                groups.computeIfAbsent(groupFor(child.getKey()), label -> new ArrayList<>()).add(child);
            }
        }

        int order = 1;
        for (Map.Entry<Integer, List<Preference>> entry : groups.entrySet()) {
            PreferenceCategory header = new PreferenceCategory(context);
            header.setOrder(order++);
            header.setTitle(entry.getKey());
            header.setLayoutResource(R.layout.detail_group_heading);
            screen.addPreference(header);
            List<Preference> rows = entry.getValue();
            for (int i = 0; i < rows.size(); i++) {
                Preference row = rows.get(i);
                row.setOrder(i);
                row.setLayoutResource(rows.size() == 1 ? R.layout.detail_row_single : i == 0 ?
                        R.layout.detail_row_first : i == rows.size() - 1 ? R.layout.detail_row_last : R.layout.detail_row_middle);
                row.setWidgetLayoutResource(row instanceof CheckBoxPreference ? R.layout.detail_checkbox_widget : R.layout.detail_choice_widget);
                setShortLabel(row);
                header.addPreference(row);
            }
        }
    }

    private int groupFor(String rawKey) {
        String key = rawKey == null ? "" : rawKey;
        switch (this) {
            case PICTURE: return key.equals("frame_pacing") || key.equals("checkbox_stretch_video") ? R.string.detail_video_group : R.string.detail_network_group;
            case SOUND: return R.string.detail_sound_group;
            case GAMEPAD:
                if (key.contains("usb_")) return R.string.detail_usb_group;
                return key.contains("vibrate") || key.contains("motion_") ? R.string.detail_pad_feedback_group : R.string.detail_pad_group;
            case TOUCH: return key.contains("shortcut_rail") ? R.string.detail_shortcuts_group : R.string.detail_pointer_group;
            case OVERLAY: return R.string.detail_overlay_group;
            case HOST: return R.string.detail_host_group;
            case APPEARANCE: return key.equals("list_background_timeout") ? R.string.detail_background_group : R.string.detail_appearance_group;
            default:
                return key.contains("hdr") || key.contains("full_range") || key.contains("refresh_rate") ? R.string.detail_video_group : R.string.detail_diagnostics_group;
        }
    }

    private static void setShortLabel(Preference row) {
        if (row.getKey() == null) return;
        switch (row.getKey()) {
            case "checkbox_enable_audiofx":
                row.setTitle(R.string.detail_eq);
                row.setSummary(R.string.detail_eq_help);
                break;
            case "list_auto_bitrate_mode": row.setTitle(R.string.detail_mode_label); break;
            case "checkbox_save_metered_data": row.setTitle(R.string.detail_metered); break;
            case "seekbar_bitrate_kbps": row.setTitle(R.string.detail_size_label); break;
            case "checkbox_shortcut_rail":
                row.setTitle(R.string.detail_shortcuts_label);
                row.setSummary(R.string.detail_shortcuts_help);
                break;
            case "multiselect_shortcut_rail_actions": row.setTitle(R.string.detail_choose_shortcuts); break;
            case "checkbox_local_cursor": row.setTitle(R.string.detail_cursor); break;
        }
    }

    String option(Context context, String value, CharSequence fallback) {
        switch (value) {
            case "2": return "2.0";
            case "51": return "5.1";
            case "71": return "7.1";
            case "auto": return context.getString(R.string.detail_auto);
            case "forceav1": return "AV1";
            case "forceh265": return "HEVC";
            case "neverh265": return "H.264";
            default: return fallback.toString();
        }
    }

    int helpFor(ListPreference choice) {
        if (this == ADVANCED) return R.string.detail_codec_hint;
        if ("71".equals(choice.getValue())) return R.string.detail_sound_71_hint;
        if ("51".equals(choice.getValue())) return R.string.detail_sound_51_hint;
        return R.string.detail_sound_2_hint;
    }
}
