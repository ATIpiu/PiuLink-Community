package com.limelight.piulink;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.Notification;
import android.app.PendingIntent;
import android.app.ProgressDialog;
import android.content.Context;
import android.content.Intent;
import android.os.Looper;
import android.view.ContextMenu;
import android.widget.AdapterView;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.TextView;
import android.widget.Switch;
import android.view.View;

import com.limelight.AppView;
import com.limelight.Game;
import com.limelight.PcView;
import com.limelight.R;
import com.limelight.ShortcutTrampoline;
import com.limelight.binding.crypto.AndroidCryptoProvider;
import com.limelight.computers.ComputerDatabaseManager;
import com.limelight.computers.ComputerManagerService;
import com.limelight.grid.AppGridAdapter;
import com.limelight.grid.PcGridAdapter;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.nvstream.jni.MoonBridge;
import com.limelight.piulink.ssh.TransferActivity;
import com.limelight.preferences.AddComputerManually;
import com.limelight.preferences.PreferenceConfiguration;

import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.Implementation;
import org.robolectric.annotation.Implements;
import org.robolectric.shadows.ShadowToast;

import java.io.IOException;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.URI;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;

import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;
import static org.robolectric.util.ReflectionHelpers.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 34, qualifiers = "zh-rCN-w391dp-h802dp-port-mdpi",
        instrumentedPackages = {"com.limelight.nvstream.jni"},
        shadows = {MobileFeatureAuditTest.NoNativeMedia.class})
public class MobileFeatureAuditTest {
    // These UI cases do not start a decoder or open a native streaming session.
    @Implements(value = MoonBridge.class, isInAndroidSdk = false)
    public static class NoNativeMedia {
        @Implementation public static void __staticInitializer__() { }
    }

    private static Object invoke(Object target, String name, Class<?>[] types, Object... arguments) throws Exception {
        Method method = target.getClass().getDeclaredMethod(name, types);
        method.setAccessible(true);
        return method.invoke(target, arguments);
    }

    private static ComputerDetails computer() {
        ComputerDetails pc = new ComputerDetails();
        pc.uuid = "01fab18d-231c-46de-b174-6f04327c982e";
        pc.name = "Fixture PC";
        pc.manualAddress = new ComputerDetails.AddressTuple("127.0.0.1", 47989);
        pc.pairState = PairingManager.PairState.PAIRED;
        pc.state = ComputerDetails.State.ONLINE;
        return pc;
    }

    private static ContextMenu openMenu(AppView activity, int position) throws Exception {
        ContextMenu menu = (ContextMenu) Class.forName("com.android.internal.view.menu.ContextMenuBuilder")
                .getConstructor(Context.class).newInstance(activity);
        FrameLayout row = new FrameLayout(activity);
        activity.onCreateContextMenu(menu, row, new AdapterView.AdapterContextMenuInfo(row, position, position));
        return menu;
    }

    @Test public void aScreenChangeIsCommittedOnlyAfterHostPreparation() throws Exception {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        android.content.SharedPreferences prefs = activity.getSharedPreferences("monitor_selection", Context.MODE_PRIVATE);
        prefs.edit().putString("pc:device", "a").apply();
        Method guard = MonitorSelection.class.getDeclaredMethod("guard", Activity.class, MonitorSelection.Selected.class);
        guard.setAccessible(true);
        MonitorSelection.Selected selected = (MonitorSelection.Selected) guard.invoke(null, activity,
                (MonitorSelection.Selected) (i, w, h, fps, id) -> fail("No Host acknowledgement yet"));
        int[] stopped = {0};
        setField(selected, "streamSwitch", (MonitorSelection.StreamSwitch) (end, after) -> { assertTrue(end); stopped[0]++; });
        Method save = MonitorSelection.class.getDeclaredMethod("saveAndAccept", Activity.class, String.class,
                MonitorSelection.Selected.class, DisplayInventoryClient.Display.class, boolean.class,
                int.class, int.class, int.class, int.class);
        save.setAccessible(true);
        DisplayInventoryClient.Display first = new DisplayInventoryClient.Display(0, "a", "Main", true, false, 1920, 1080, false);
        DisplayInventoryClient.Display other = new DisplayInventoryClient.Display(1, "b", "Other", false, false, 1920, 1080, false);
        save.invoke(null, activity, "pc", selected, first, false, 1920, 1080, 60, 0);
        assertEquals(0, stopped[0]);
        save.invoke(null, activity, "pc", selected, other, false, 1920, 1080, 60, 0);
        org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_NEGATIVE).performClick();
        assertEquals(0, stopped[0]);
        save.invoke(null, activity, "pc", selected, other, false, 1920, 1080, 60, 0);
        org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        shadowOf(Looper.getMainLooper()).idle();
        assertEquals(1, stopped[0]);
        assertEquals("a", prefs.getString("pc:device", ""));
    }

    @Test public void sessionScreenPanelProtectsCaptureAndOffersOnlyAvailableActions() throws Exception {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        boolean[] switchRequested = {false};
        java.lang.reflect.Constructor<SessionDisplayControls> constructor = SessionDisplayControls.class.getDeclaredConstructor(Activity.class, String.class, String.class, Runnable.class);
        constructor.setAccessible(true);
        SessionDisplayControls panel = constructor.newInstance(activity, null, "test", (Runnable) () -> switchRequested[0] = true);
        AlertDialog dialog = getField(panel, "dialog");
        dialog.show();
        org.json.JSONArray rows = new org.json.JSONArray()
                .put(new org.json.JSONObject().put("name", "Main").put("key", "a").put("active", true).put("capture", true))
                .put(new org.json.JSONObject().put("name", "Side").put("key", "b").put("active", false));
        org.json.JSONObject state = new org.json.JSONObject().put("outputs", rows).put("createStatus", "driver_required");
        panel.render(state);
        android.widget.LinearLayout content = getField(panel, "content");
        assertFalse(content.getChildAt(0) instanceof Button);
        Switch enable = (Switch) content.getChildAt(1);
        assertTrue(enable.isEnabled());
        assertFalse(enable.isChecked());
        assertTrue(enable.getText().toString().contains("Side"));
        assertFalse(content.getChildAt(3).isEnabled());
        enable.performClick(); // Without a Host acknowledgement, keep the last verified state and allow a read.
        assertSame(enable, content.getChildAt(1));
        assertFalse(enable.isChecked());
        assertFalse(enable.isEnabled());
        assertTrue(dialog.getButton(AlertDialog.BUTTON_NEUTRAL).isEnabled());
        assertTrue(dialog.isShowing());
        rows.getJSONObject(1).put("active", true);
        state.put("createStatus", "available").put("virtualCount", 1);
        panel.render(state);
        assertTrue(((Switch) content.getChildAt(1)).isChecked());
        Button create = (Button) content.getChildAt(3);
        assertTrue(create.isEnabled());
        create.performClick();
        AlertDialog confirmation = org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog();
        assertNotSame(dialog, confirmation);
        confirmation.getButton(AlertDialog.BUTTON_NEGATIVE).performClick();
        assertFalse(switchRequested[0]);
        ((Button) getField(panel, "switchDisplay")).performClick();
        assertTrue(switchRequested[0]);
        assertFalse(dialog.isShowing());
    }

    @Test public void unchangedScreenPollPreservesSwitchAndLostCaptureDisablesActions() throws Exception {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        java.lang.reflect.Constructor<SessionDisplayControls> constructor = SessionDisplayControls.class
                .getDeclaredConstructor(Activity.class, String.class, String.class, Runnable.class);
        constructor.setAccessible(true);
        SessionDisplayControls panel = constructor.newInstance(activity, null, "test", (Runnable) () -> {});
        AlertDialog dialog = getField(panel, "dialog");
        dialog.show();
        org.json.JSONObject main = new org.json.JSONObject().put("name", "Main").put("key", "a")
                .put("active", true).put("capture", true);
        org.json.JSONObject side = new org.json.JSONObject().put("name", "Side").put("key", "b").put("active", false);
        org.json.JSONObject state = new org.json.JSONObject().put("outputs", new org.json.JSONArray().put(main).put(side))
                .put("createStatus", "available").put("virtualCount", 1);
        panel.render(state);
        android.widget.LinearLayout content = getField(panel, "content");
        Switch original = (Switch) content.getChildAt(1);
        panel.render(new org.json.JSONObject(state.toString()));
        assertSame(original, content.getChildAt(1));
        side.put("active", true);
        panel.render(state);
        assertTrue(((Switch) content.getChildAt(1)).isChecked());

        Method failed = SessionDisplayControls.class.getDeclaredMethod("showError", String.class, boolean.class);
        failed.setAccessible(true);
        failed.invoke(panel, "", true);
        assertFalse(content.getChildAt(1).isEnabled());
        assertTrue(dialog.getButton(AlertDialog.BUTTON_NEUTRAL).isEnabled());
        panel.render(state);
        assertTrue(content.getChildAt(1).isEnabled());

        main.put("active", false);
        panel.render(state);
        Switch other = (Switch) content.getChildAt(0);
        assertTrue(other.isChecked());
        assertFalse(other.isEnabled());
        assertFalse(content.getChildAt(2).isEnabled());
        assertTrue(((Button) getField(panel, "switchDisplay")).isEnabled());
        dialog.dismiss();
        shadowOf(Looper.getMainLooper()).idle();
        java.util.concurrent.ExecutorService requests = getField(panel, "requests");
        assertTrue(requests.isShutdown());
    }

    @Test public void appActionKeepsItsTargetAcrossReorderAndRemoval() throws Exception {
        AppView view = Robolectric.buildActivity(AppView.class).get();
        ComputerDetails pc = computer();
        AppGridAdapter adapter = new AppGridAdapter(view, new PreferenceConfiguration(), pc, "fixture", false);
        setField(view, "appGridAdapter", adapter);
        setField(view, "uuidString", pc.uuid);
        List<AppView.AppObject> rows = getField(adapter, "allApps");
        AppView.AppObject alpha = new AppView.AppObject(new NvApp("Alpha", 100, false));
        AppView.AppObject beta = new AppView.AppObject(new NvApp("Beta", 200, false));
        rows.add(beta);
        adapter.refreshLibrary();

        ContextMenu beforeReorder = openMenu(view, 0);
        int hide = getStaticField(AppView.class, "HIDE_APP_ID");
        rows.add(alpha);
        adapter.refreshLibrary();
        assertSame(alpha, adapter.getItem(0));
        assertTrue(beforeReorder.performIdentifierAction(hide, 0));
        assertTrue(beta.isHidden);
        assertFalse(alpha.isHidden);
        assertEquals(1, adapter.getCount());

        ContextMenu beforeRemoval = openMenu(view, 0);
        adapter.removeApp(alpha);
        assertTrue(beforeRemoval.performIdentifierAction(hide, 0));
        assertEquals(view.getString(R.string.applist_changed), ShadowToast.getTextOfLatestToast());
    }

    @Test public void homeDesktopButtonUsesTheBoundPairedHostAndClearsRecycledCards() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        ComputerDetails first = computer(), second = computer();
        first.serverCert = new AndroidCryptoProvider(activity).getClientCertificate();
        first.pairState = PairingManager.PairState.PAIRED;
        first.name = "A";
        second.name = "B";
        ComputerDetails[] tapped = {null};
        ComputerDetails[] libraryTarget = {null};
        PcGridAdapter adapter = new PcGridAdapter(activity, PreferenceConfiguration.readPreferences(activity),
                pc -> tapped[0] = pc, pc -> libraryTarget[0] = pc);
        adapter.addComputer(new PcView.ComputerObject(first));
        adapter.addComputer(new PcView.ComputerObject(second));
        FrameLayout parent = new FrameLayout(activity);
        View card = adapter.getView(0, null, parent);
        card.findViewById(R.id.pcOpenLibrary).performClick();
        assertSame(first, libraryTarget[0]);
        assertNull(tapped[0]);
        View button = card.findViewById(R.id.pcOpenDesktop);
        assertEquals(View.VISIBLE, button.getVisibility());
        button.performClick();
        assertSame(first, tapped[0]);
        tapped[0] = null;
        assertSame(card, adapter.getView(1, card, parent));
        assertEquals(View.GONE, button.getVisibility());
        button.performClick();
        assertNull(tapped[0]);
        first.state = ComputerDetails.State.OFFLINE;
        adapter.getView(0, card, parent);
        assertEquals(View.GONE, button.getVisibility());
    }

    @Test @Config(qualifiers = "zh-rCN-w320dp-h700dp-port-mdpi")
    public void narrowPhoneHasOneBottomNavigationAndBothCardActionsFit() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        shadowOf(activity.getPackageManager()).setSystemFeature(android.content.pm.PackageManager.FEATURE_TOUCHSCREEN, true);
        activity.setContentView(R.layout.activity_pc_touch);
        HomeTabs home = new HomeTabs(activity);
        android.widget.LinearLayout[] items = getField(home, "tabs");
        android.widget.LinearLayout bar = (android.widget.LinearLayout) items[0].getParent();
        assertEquals(android.widget.LinearLayout.HORIZONTAL, bar.getOrientation());
        assertNull(activity.findViewById(R.id.terminalButton));
        home.select(2);
        assertTrue(items[2].isSelected());
        assertTrue(home.back());
        assertTrue(items[0].isSelected());

        android.view.ViewGroup card = (android.view.ViewGroup) activity.getLayoutInflater()
                .inflate(R.layout.pc_touch_item, new FrameLayout(activity), false);
        card.measure(View.MeasureSpec.makeMeasureSpec(284, View.MeasureSpec.EXACTLY),
                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED));
        card.layout(0, 0, card.getMeasuredWidth(), card.getMeasuredHeight());
        for (int id : new int[] {R.id.pcOpenDesktop, R.id.pcOpenLibrary}) {
            View action = card.findViewById(id);
            android.graphics.Rect rect = new android.graphics.Rect(0, 0, action.getWidth(), action.getHeight());
            card.offsetDescendantRectToMyCoords(action, rect);
            assertTrue(action.getHeight() >= 48);
            assertTrue(rect.left >= 0 && rect.right <= card.getWidth());
        }
    }

    @Test @Config(qualifiers = "zh-rCN-w1080dp-h720dp-land-mdpi")
    public void landscapeTabletKeepsOneNavigationRailAcrossPages() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        shadowOf(activity.getPackageManager()).setSystemFeature(android.content.pm.PackageManager.FEATURE_TOUCHSCREEN, true);
        activity.setContentView(R.layout.activity_pc_touch);
        HomeTabs home = new HomeTabs(activity);
        android.widget.LinearLayout[] items = getField(home, "tabs");
        android.widget.LinearLayout bar = (android.widget.LinearLayout) items[0].getParent();
        assertEquals(android.widget.LinearLayout.VERTICAL, bar.getOrientation());
        assertEquals(3, items.length);
        home.select(2);
        assertTrue(items[2].isShown());
        assertTrue(home.back());
        assertTrue(items[0].isSelected());
    }

    public static class SliderTestActivity extends android.preference.PreferenceActivity { }

    @Test public void bitrateRowAndSliderUseTheStreamingValueBeforeTheFirstSave() {
        SliderTestActivity activity = Robolectric.buildActivity(SliderTestActivity.class).setup().get();
        activity.addPreferencesFromResource(R.xml.preferences);
        com.limelight.preferences.SeekBarPreference bitrate =
                (com.limelight.preferences.SeekBarPreference) activity.findPreference("seekbar_bitrate_kbps");
        bitrate.setLayoutResource(R.layout.detail_row_single);
        bitrate.setWidgetLayoutResource(R.layout.detail_choice_widget);
        android.content.SharedPreferences store = activity.getPreferenceManager().getSharedPreferences();
        String key = bitrate.getKey();
        store.edit().remove(key).remove("seekbar_bitrate").apply();
        int effective = PreferenceConfiguration.readPreferences(activity).bitrate;
        assertTrue(effective > 0);
        FrameLayout parent = new FrameLayout(activity);
        TextView label = bitrate.getView(null, parent).findViewById(R.id.detailChoiceValue);
        assertEquals(String.format(java.util.Locale.getDefault(), "%.1f Mbps", effective / 1000f), label.getText());
        bitrate.showDialog(null);
        android.widget.SeekBar slider = getField(bitrate, "seekBar");
        assertEquals(effective, slider.getProgress());
        slider.setProgress(42000);
        bitrate.setOnPreferenceChangeListener((pref, value) -> false);
        ((AlertDialog) bitrate.getDialog()).getButton(AlertDialog.BUTTON_POSITIVE).performClick();
        assertFalse(store.contains(key));
        store.edit().putInt("seekbar_bitrate", 12).apply();
        label = bitrate.getView(null, parent).findViewById(R.id.detailChoiceValue);
        assertEquals("12.0 Mbps", label.getText());
    }

    @Test public void detailChoicesUseThePreferenceListenerAndItsActualEntries() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        FeaturedSettingView card = (FeaturedSettingView) activity.getLayoutInflater()
                .inflate(R.layout.detail_feature_preference, new FrameLayout(activity), false);
        android.preference.ListPreference sound = new com.limelight.preferences.TvListPreference(activity);
        sound.setKey("list_audio_config");
        sound.setEntries(new String[]{"Stereo", "7.1 Surround"});
        sound.setEntryValues(new String[]{"2", "71"});
        sound.setValue("2");
        card.bind(sound);
        android.widget.LinearLayout bar = card.findViewById(R.id.detailModeBar);
        assertEquals(2, bar.getChildCount());
        assertEquals("7.1", ((Button) bar.getChildAt(1)).getText().toString());
        sound.setOnPreferenceChangeListener((preference, value) -> false);
        bar.getChildAt(1).performClick();
        assertEquals("2", sound.getValue());
        assertTrue(bar.getChildAt(0).isSelected());
        sound.setOnPreferenceChangeListener((preference, value) -> true);
        bar.getChildAt(1).performClick();
        assertEquals("71", sound.getValue());
        assertEquals("7.1 Surround", ((TextView) card.findViewById(R.id.detailFeatureValue)).getText().toString());

        android.preference.CheckBoxPreference touch = new com.limelight.preferences.DetailCheckPreference(activity, null);
        touch.setKey("checkbox_touchscreen_trackpad");
        touch.setChecked(true);
        card.bind(touch);
        assertTrue(bar.getChildAt(0).isSelected());
        bar.getChildAt(1).performClick();
        assertFalse(touch.isChecked());
        assertTrue(bar.getChildAt(1).isSelected());
    }

    @Test public void quickQualityUsesTheSavedPreferenceAndRespectsFixedBitrate() {
        Activity activity = Robolectric.buildActivity(Activity.class).setup().get();
        View[] entries = new View[8];
        int[] opened = {-1};
        MobileSettingsOverview overview = new MobileSettingsOverview(activity, section -> opened[0] = section, entries);
        android.content.SharedPreferences prefs = android.preference.PreferenceManager.getDefaultSharedPreferences(activity);
        prefs.edit().putBoolean("checkbox_auto_bitrate", true).putString("list_auto_bitrate_mode", "balanced").apply();
        overview.refresh();
        android.widget.LinearLayout modes = overview.findViewById(R.id.settingsModes);
        assertTrue(modes.getChildAt(1).isSelected());
        modes.getChildAt(2).performClick();
        assertEquals("stable", prefs.getString("list_auto_bitrate_mode", ""));
        assertTrue(modes.getChildAt(2).isSelected());
        for (int index = 0; index < entries.length; index++) {
            entries[index].performClick();
            assertEquals(index, opened[0]);
        }
        prefs.edit().putBoolean("checkbox_auto_bitrate", false).putInt("seekbar_bitrate_kbps", 12500).apply();
        overview.refresh();
        assertEquals(View.GONE, modes.getVisibility());
        assertEquals("12.5 Mbps", ((TextView) overview.findViewById(R.id.settingsBitrateValue)).getText().toString());
        PreferenceConfiguration.setAutomaticBitrateMode(activity, "quality");
        assertEquals("stable", prefs.getString("list_auto_bitrate_mode", ""));
    }

    @Test public void librarySearchAndFilterSurviveRecreationAndClearWhenClosed() {
        Context context = RuntimeEnvironment.getApplication();
        shadowOf(context.getPackageManager()).setSystemFeature(android.content.pm.PackageManager.FEATURE_TOUCHSCREEN, true);
        Intent intent = new Intent(context, AppView.class)
                .putExtra(AppView.UUID_EXTRA, "library-test").putExtra(AppView.NAME_EXTRA, "Test PC");
        ActivityController<AppView> controller = Robolectric.buildActivity(AppView.class, intent).create();
        AppView library = controller.get();
        library.findViewById(R.id.appLibraryTools).performClick();
        ((TextView) library.findViewById(R.id.appSearchInput)).setText("Steam");
        library.findViewById(R.id.filterRecentButton).performClick();
        android.os.Bundle state = new android.os.Bundle();
        controller.saveInstanceState(state).destroy();

        controller = Robolectric.buildActivity(AppView.class, intent).create(state);
        library = controller.get();
        TextView search = library.findViewById(R.id.appSearchInput);
        assertEquals(View.VISIBLE, search.getVisibility());
        assertEquals("Steam", search.getText().toString());
        assertTrue(library.findViewById(R.id.filterRecentButton).isSelected());
        library.findViewById(R.id.appLibraryTools).performClick();
        assertEquals(View.GONE, search.getVisibility());
        assertEquals("", search.getText().toString());
        assertTrue(library.findViewById(R.id.filterAllButton).isSelected());
        controller.destroy();
    }

    @Test public void desktopEntryRequiresOneVisibleDesktopPublishedByTheHost() throws Exception {
        AppView view = Robolectric.buildActivity(AppView.class).get();
        NvApp desktop = new NvApp("Desktop", 17, false);
        NvApp translated = new NvApp("桌面", 21, false);
        NvApp game = new NvApp("Desktop Arcade", 23, false);
        Class<?>[] arg = {List.class};
        assertSame(desktop, invoke(view, "findPublishedDesktop", arg, java.util.Arrays.asList(game, desktop)));
        assertNull(invoke(view, "findPublishedDesktop", arg, java.util.Arrays.asList(game)));
        assertNull(invoke(view, "findPublishedDesktop", arg, java.util.Arrays.asList(desktop, translated)));
        setField(view, "hiddenAppIds", new java.util.HashSet<>(java.util.Collections.singleton(17)));
        assertSame(translated, invoke(view, "findPublishedDesktop", arg, java.util.Arrays.asList(desktop, translated)));
        assertNull(invoke(view, "findPublishedDesktop", arg, java.util.Arrays.asList(desktop, game)));
    }

    @Test public void desktopShortcutWaitsForHostStatusAndDoesNotReplaceAnUnconfirmedApp() throws Exception {
        AppView view = Robolectric.buildActivity(AppView.class).get();
        view.setIntent(new Intent().putExtra("OpenPublishedDesktop", true));
        ComputerDetails pc = computer();
        pc.pairState = PairingManager.PairState.PAIRED;
        pc.serverCert = new AndroidCryptoProvider(view).getClientCertificate();
        ComputerManagerService service = Robolectric.buildService(ComputerManagerService.class).get();
        setField(view, "managerBinder", service.new ComputerManagerBinder());
        setField(view, "uuidString", pc.uuid);
        setField(view, "inForeground", true);
        invoke(view, "handleShortcut", new Class<?>[]{ComputerDetails.class}, pc);
        assertEquals(Boolean.FALSE, getField(view, "shortcutHandled"));
        List<NvApp> apps = java.util.Collections.singletonList(new NvApp("Desktop", 17, false));
        Class<?>[] args = {ComputerDetails.class, List.class, Integer.class};
        invoke(view, "openPublishedDesktop", args, pc, apps, null);
        assertEquals(Boolean.TRUE, getField(view, "shortcutHandled"));
        assertEquals(view.getString(R.string.pc_desktop_unavailable), ShadowToast.getTextOfLatestToast());

        setField(view, "shortcutHandled", false);
        invoke(view, "openPublishedDesktop", args, pc, apps, 91);
        AlertDialog confirmation = org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog();
        assertTrue(confirmation.isShowing());
        confirmation.getButton(AlertDialog.BUTTON_NEGATIVE).performClick();
        assertNull(shadowOf(view).getNextStartedActivity());
    }

    @Test public void completedDisconnectRefreshesOnlyTheVisibleLibraryForThatHost() throws Exception {
        ComputerDetails pc = computer();
        Intent library = new Intent().putExtra(AppView.UUID_EXTRA, pc.uuid);
        ActivityController<AppView> controller = Robolectric.buildActivity(AppView.class, library)
                .create().start().resume();
        AppView view = controller.get();
        ComputerManagerService service = Robolectric.buildService(ComputerManagerService.class).get();
        int[] reads = {0};
        setField(view, "poller", service.new ApplistPoller(pc, true) {
            @Override public void pollNow() { reads[0]++; }
        });
        Class<?>[] arguments = {String.class};
        invoke(view.getApplication(), "refreshRunningAppForHost", arguments, "other-host");
        assertEquals(0, reads[0]);
        invoke(view.getApplication(), "refreshRunningAppForHost", arguments, pc.uuid);
        assertEquals(1, reads[0]);
        controller.pause();
        invoke(view.getApplication(), "refreshRunningAppForHost", arguments, pc.uuid);
        assertEquals(1, reads[0]);
        controller.stop().destroy();
    }

    @Test public void shortcutsPassTheTargetToTheLibraryOnlyOnce() throws Exception {
        for (boolean gameShortcut : new boolean[]{true, false}) {
            ShortcutTrampoline shortcut = Robolectric.buildActivity(ShortcutTrampoline.class).get();
            ComputerManagerService service = Robolectric.buildService(ComputerManagerService.class).get();
            ComputerManagerService.ComputerManagerBinder binder = service.new ComputerManagerBinder();
            ComputerDetails pc = computer();
            pc.runningGameId = 11;
            if (gameShortcut) setField(shortcut, "app", new NvApp("Meeting", 7, false));
            setField(shortcut, "managerBinder", binder);
            Class<?>[] types = {ComputerManagerService.ComputerManagerBinder.class, ComputerDetails.class};

            invoke(shortcut, "openLibrary", types, binder, pc);
            Intent launch = shadowOf(shortcut).getNextStartedActivity();
            assertEquals(AppView.class.getName(), launch.getComponent().getClassName());
            assertEquals(pc.uuid, launch.getStringExtra(AppView.UUID_EXTRA));
            if (gameShortcut) {
                assertEquals(7, launch.getIntExtra(Game.EXTRA_APP_ID, -1));
            } else {
                assertEquals(11, launch.getIntExtra("ShortcutResumeAppId", -1));
                assertEquals(PcView.class.getName(), shadowOf(shortcut).getNextStartedActivity().getComponent().getClassName());
            }
            invoke(shortcut, "openLibrary", types, binder, pc);
            assertNull(shadowOf(shortcut).getNextStartedActivity());
        }
    }

    @Test public void invalidOrRepeatedAddressSubmissionKeepsTheFormUsable() {
        AddComputerManually activity = Robolectric.buildActivity(AddComputerManually.class).create().start().resume().get();
        TextView input = activity.findViewById(R.id.hostTextView);
        setField(activity, "serviceBound", true);
        setField(activity, "managerBinder", null);
        input.setText("pc:65536");
        activity.findViewById(R.id.addPcButton).performClick();
        assertEquals(activity.getString(R.string.addpc_invalid_address), input.getError().toString());
        assertNull(getField(activity, "pendingAddress"));

        input.setText("pc.example:47989");
        activity.findViewById(R.id.addPcButton).performClick();
        ProgressDialog progress = getField(activity, "progress");
        assertTrue(progress.isShowing());
        input.setText("another.example");
        activity.findViewById(R.id.addPcButton).performClick();
        assertSame(progress, getField(activity, "progress"));
        URI pending = getField(activity, "pendingAddress");
        assertEquals("pc.example", pending.getHost());

        progress.cancel();
        shadowOf(Looper.getMainLooper()).idle();
        assertNull(getField(activity, "pendingAddress"));
        assertFalse(activity.isFinishing());
    }

    @Test public void sharingCanVisitPairingAndReturnToTheOriginalContent() throws Exception {
        ComputerDetails pc = computer();
        pc.pairState = PairingManager.PairState.NOT_PAIRED;
        ComputerDatabaseManager database = new ComputerDatabaseManager(RuntimeEnvironment.getApplication());
        try {
            assertTrue(database.updateComputer(pc));
        } finally {
            database.close();
        }
        Intent send = new Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, "fixture share");
        TransferActivity share = Robolectric.buildActivity(TransferActivity.class, send).create().get();
        Button action = getField(share, "action");
        assertEquals("去配对电脑", action.getText());
        action.performClick();
        Intent pairing = shadowOf(share).getNextStartedActivity();
        assertEquals(PcView.class.getName(), pairing.getComponent().getClassName());
        assertEquals(PcView.ACTION_SHOW_COMPUTERS, pairing.getAction());

        invoke(share, "onActivityResult", new Class<?>[]{int.class, int.class, Intent.class},
                1, Activity.RESULT_CANCELED, null);
        assertEquals("选择接收电脑", action.getText());
        assertEquals("fixture share", getField(share, "pendingText"));
        action.performClick();
        assertEquals("去配对电脑", action.getText());
        assertFalse(share.isFinishing());
    }

    @Test public void cancellingThePinDialogClosesTheWaitingCall() throws Exception {
        PcView view = Robolectric.buildActivity(PcView.class).get();
        InetAddress loopback = InetAddress.getByName("127.0.0.1");
        try (ServerSocket server = new ServerSocket(0, 1, loopback)) {
            server.setSoTimeout(4000);
            NvHTTP client = new NvHTTP(new ComputerDetails.AddressTuple("127.0.0.1", server.getLocalPort()),
                    0, "fixture", null, new AndroidCryptoProvider(view));
            ExecutorService worker = Executors.newSingleThreadExecutor();
            try {
                Future<?> shown = worker.submit(() -> {
                    invoke(view, "showPairingPinDialogAndWait", new Class<?>[]{String.class, NvHTTP.class}, "1234", client);
                    return null;
                });
                long deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(4);
                while (!shown.isDone() && System.nanoTime() < deadline) {
                    shadowOf(Looper.getMainLooper()).idle();
                    Thread.sleep(10);
                }
                shown.get(1, TimeUnit.SECONDS);
                AlertDialog dialog = getField(view, "pairingPinDialog");
                assertTrue(dialog.isShowing());
                Callable<Boolean> requestCancelled = () -> {
                    try {
                        invoke(client, "executePairingCommand", new Class<?>[]{String.class, boolean.class}, "fixture=1", false);
                        return false;
                    } catch (InvocationTargetException result) {
                        return result.getCause() instanceof IOException;
                    }
                };

                Future<Boolean> waiting = worker.submit(requestCancelled);
                try (Socket stalled = server.accept()) {
                    dialog.getButton(AlertDialog.BUTTON_NEGATIVE).performClick();
                    shadowOf(Looper.getMainLooper()).idle();
                    assertTrue(waiting.get(2, TimeUnit.SECONDS));
                    assertTrue(worker.submit(requestCancelled).get(1, TimeUnit.SECONDS));
                }
                assertTrue((boolean) getField(view, "pairingCancelled"));
            } finally {
                worker.shutdownNow();
            }
        }
    }

    @Test public void staleNotificationsCannotStopALaterStreamOrTheForegroundStream() throws Exception {
        BackgroundStreamService service = Robolectric.buildService(BackgroundStreamService.class).create().get();
        Game game = Robolectric.buildActivity(Game.class).get();
        Game.activeInstance = game;
        try {
            PendingIntent[] stopActions = new PendingIntent[2];
            for (int i = 0; i < stopActions.length; i++) {
                Intent start = new Intent().putExtra(BackgroundStreamService.EXTRA_SESSION, "entry" + i)
                        .putExtra(BackgroundStreamService.EXTRA_TIMEOUT_MINUTES, 0);
                service.onStartCommand(start, 0, i + 1);
                Notification notification = shadowOf(service).getLastForegroundNotification();
                stopActions[i] = notification.actions[0].actionIntent;
            }
            assertNotEquals(stopActions[0], stopActions[1]);
            Intent old = shadowOf(stopActions[0]).getSavedIntent();
            Intent current = shadowOf(stopActions[1]).getSavedIntent();
            assertEquals("entry0", old.getStringExtra(BackgroundStreamService.EXTRA_SESSION));
            setField(game, "backgroundSession", "entry1");
            BackgroundStreamActionReceiver receiver = new BackgroundStreamActionReceiver();
            receiver.onReceive(game, old);
            assertFalse(game.isFinishing());
            assertEquals("entry1", getField(game, "backgroundSession"));

            invoke(game, "stopBackgroundStreamService", new Class<?>[0]);
            receiver.onReceive(game, current);
            assertFalse(game.isFinishing());
            assertNull(getField(game, "backgroundSession"));
        } finally {
            Game.activeInstance = null;
            service.onDestroy();
        }
    }
}
