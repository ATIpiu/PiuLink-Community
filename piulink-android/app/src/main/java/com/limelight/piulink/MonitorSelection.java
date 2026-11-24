package com.limelight.piulink;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.os.SystemClock;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Display;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ListView;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import com.limelight.R;
import com.limelight.LimeLog;
import com.limelight.preferences.PreferenceConfiguration;

import java.util.ArrayList;
import java.util.List;

/** Shows the real display inventory published by PiuLink Host. */
public final class MonitorSelection {
    public interface Selected { void accept(int index, int width, int height, int fps, String requestId); }
    private static final String PREFERENCES = "monitor_selection";
    private static final String DEVICE_SUFFIX = ":device";
    private static final String RESOLUTION_SUFFIX = ":virtual_resolution";
    private static final String REFRESH_RATE_SUFFIX = ":virtual_refresh_rate";
    private static final String EXCLUSIVE_SUFFIX = ":exclusive";
    private static final String SCALE_SUFFIX = ":windows_scale:";

    private MonitorSelection() { }
    private static final java.util.Set<String> warmingHosts =
            java.util.concurrent.ConcurrentHashMap.newKeySet();

    public static void warmInventory(Activity activity, String address) {
        if (address == null || address.trim().isEmpty() || !warmingHosts.add(address)) return;
        new Thread(() -> {
            long started = android.os.SystemClock.elapsedRealtime();
            try {
                DisplayInventoryClient.fetchCached(activity, address);
                LimeLog.info("PiuLink display inventory prewarmed in " +
                        (android.os.SystemClock.elapsedRealtime() - started) + " ms");
            } catch (Exception error) {
                LimeLog.warning("Display inventory prewarm deferred: " + error.getClass().getSimpleName());
            } finally {
                warmingHosts.remove(address);
            }
        }, "PiuLink display prewarm").start();
    }
    private static final java.util.WeakHashMap<Activity, Integer> generations = new java.util.WeakHashMap<>();
    private static final DisplayPreparationState.Pending<Activity> preparations = new DisplayPreparationState.Pending<>();
    public static synchronized void cancel(Activity activity) {
        generations.put(activity, generation(activity) + 1);
        DisplayPreparationState pending = preparations.cancel(activity);
        if (pending != null) release(activity, pending.host, pending.requestId);
    }
    public static boolean beginSwitch(Activity activity, String address, String requestId) {
        return DisplayInventoryClient.beginSwitch(activity.getApplicationContext(), address, requestId);
    }

    public static void release(Activity activity, String address, String requestId) {
        DisplayInventoryClient.abort(activity.getApplicationContext(), address, requestId);
    }
    private static int generation(Activity activity) {
        Integer value = generations.get(activity);
        return value == null ? 0 : value;
    }
    private static synchronized GuardedSelection guard(Activity activity, Selected selected) {
        cancel(activity);
        while (selected instanceof GuardedSelection) selected = ((GuardedSelection) selected).delegate;
        return new GuardedSelection(activity, selected, generations.get(activity));
    }
    private static synchronized boolean isCurrent(Activity activity, Selected selected) {
        return !(selected instanceof GuardedSelection) ||
                ((GuardedSelection) selected).generation == generation(activity);
    }
    private static final class GuardedSelection implements Selected {
        final Activity activity; final Selected delegate; final int generation;
        Boolean gatherDraft;
        StreamSwitch streamSwitch;
        CaptureChanged captureChanged;
        String session;
        String address;
        AlertDialog progress;
        String preparedId;
        GuardedSelection(Activity activity, Selected delegate, int generation) {
            this.activity = activity; this.delegate = delegate; this.generation = generation;
        }
        @Override public void accept(int index, int width, int height, int fps, String requestId) {
            if (isCurrent(activity, this) && !activity.isFinishing() && !activity.isDestroyed())
                delegate.accept(index, width, height, fps, requestId);
        }
    }

    public static boolean isVirtualSelected(Activity activity, String host) {
        return preferences(activity).getBoolean(key(host) + ":virtual", false);
    }
    public static boolean gathersWindows(Activity activity, String host) {
        return preferences(activity).getBoolean(key(host) + ":gather_windows", false);
    }
    private static boolean usesPhysicalDisplayExclusively(Activity activity, String host) {
        return preferences(activity).getBoolean(key(host) + EXCLUSIVE_SUFFIX, false);
    }
    private static boolean desiredGather(Activity activity, String host, Selected selected) {
        Boolean draft = selected instanceof GuardedSelection ? ((GuardedSelection) selected).gatherDraft : null;
        return draft == null ? gathersWindows(activity, host) : draft;
    }

    /** Reuse the last stable device ID; only ask again if that display disappeared. */
    public static void connectDefault(Activity activity, String host, String address, Selected selected) {
        connectDefault(activity, host, address, false, selected);
    }
    public static void connectDefault(Activity activity, String host, String address, boolean virtualApp, Selected selected) {
        connectDefaultRequest(activity, host, address, virtualApp, guard(activity, selected));
    }
    private static void connectDefaultRequest(Activity activity, String host, String address, boolean virtualApp, Selected selected) {
        AlertDialog loading = connectionDialog(activity, true)
                .setTitle(R.string.screen_connecting).setMessage(R.string.monitor_loading).create();
        ((GuardedSelection) selected).progress = loading;
        loading.show();
        new Thread(() -> {
            long inventoryStarted = android.os.SystemClock.elapsedRealtime();
            DisplayInventoryClient.Inventory inventory;
            try {
                inventory = DisplayInventoryClient.fetchCached(activity, address);
                LimeLog.info("PiuLink launch inventory ready in " +
                        (android.os.SystemClock.elapsedRealtime() - inventoryStarted) + " ms");
            }
            catch (Exception error) {
                activity.runOnUiThread(() -> {
                    if (!loading.isShowing()) return;
                    loading.dismiss();
                    if (!activity.isFinishing() && !activity.isDestroyed() && isCurrent(activity, selected)) {
                        connectionDialog(activity, true).setMessage(R.string.monitor_unavailable)
                                .setPositiveButton(R.string.monitor_refresh,
                                        (d, w) -> connectDefaultRequest(activity, host, address, virtualApp, selected)).show();
                    }
                });
                return;
            }
            activity.runOnUiThread(() -> {
                if (!loading.isShowing()) return;
                if (activity.isFinishing() || activity.isDestroyed() || !isCurrent(activity, selected)) { loading.dismiss(); return; }
                String saved = preferences(activity).getString(key(host) + DEVICE_SUFFIX, "");
                DisplayInventoryClient.Display target = DisplayInventoryClient.defaultDisplay(inventory, saved, virtualApp);
                if (target == null && virtualApp) {
                    loading.dismiss();
                    new AlertDialog.Builder(activity).setMessage("Host 尚未提供虚拟屏幕，请先在电脑启用虚拟显示器，再打开虚拟应用。")
                        .setPositiveButton(android.R.string.ok, null).show();
                    return;
                }
                if (target == null) {
                    loading.dismiss();
                    DisplayInventoryClient.Display current = DisplayInventoryClient.activeDesktop(inventory);
                    if (!saved.isEmpty() && current != null && !current.virtual) {
                        Runnable useDesktop = () -> {
                            ((GuardedSelection) selected).gatherDraft = false;
                            DisplayInventoryClient.Mode mode = physicalMode(activity, address, current);
                            prepareAndAccept(activity, host, address, selected, current, false,
                                    mode.width, mode.height, mode.fps, 0);
                        };
                        if (isVirtualSelected(activity, host) || usesPhysicalDisplayExclusively(activity, host)) {
                            connectionDialog(activity, true).setMessage(activity.getString(R.string.screen_missing_saved, current.name))
                                .setPositiveButton(R.string.screen_use_desktop, (d, w) -> useDesktop.run()).show();
                        } else useDesktop.run();
                        return;
                    }
                    showDisplays(activity, host, address, true, selected, inventory.exclusive, inventory.displays,
                            inventory.virtualResolutions, inventory.virtualRefreshRates);
                    return;
                }
                int width = 0, height = 0, fps = 0;
                if (target.virtual) {
                    DisplayInventoryClient.Mode mode = preferredMode(activity, host, supportedModes(activity, target));
                    if (mode == null) {
                        loading.dismiss();
                        showVirtualResolution(activity, host, address, selected, target,
                            inventory.virtualResolutions, inventory.virtualRefreshRates, true); return;
                    }
                    width = mode.width; height = mode.height; fps = mode.fps;
                } else {
                    // A previously selected virtual mode must not dictate the stream size
                    // when returning to a physical monitor. Keep its Windows mode intact.
                    DisplayInventoryClient.Mode mode = physicalMode(activity, address, target);
                    width = mode.width; height = mode.height; fps = mode.fps;
                }
                boolean exclusive = target.virtual || usesPhysicalDisplayExclusively(activity, host);
                int scalePercent = preferredScale(activity, host, target);
                prepareAndAccept(activity, host, address, selected, target, exclusive,
                        width, height, fps, scalePercent);
            });
        }, "PiuLink saved display").start();
    }

    private static DisplayInventoryClient.Mode physicalMode(Activity activity, String address,
                                                           DisplayInventoryClient.Display display) {
        PreferenceConfiguration prefs = PreferenceConfiguration.readPreferences(activity);
        DisplayInventoryClient.Mode stream = AutoBitratePolicy.physicalMode(AutoBitrateManager.routeFor(activity, address),
                display.width, display.height, prefs.autoBitrate, prefs.autoBitrateMode, prefs.saveDataOnMeteredNetwork);
        if (!prefs.autoBitrate || !com.limelight.utils.UiHelper.isTelevision(activity)) return stream;
        List<DisplayInventoryClient.Mode> outputs = new ArrayList<>();
        for (Display.Mode mode : activity.getWindowManager().getDefaultDisplay().getSupportedModes()) {
            outputs.add(new DisplayInventoryClient.Mode(mode.getPhysicalWidth(), mode.getPhysicalHeight(),
                    Math.round(mode.getRefreshRate())));
        }
        return AutoBitratePolicy.fitTvOutput(stream, outputs);
    }

    private static AlertDialog.Builder connectionDialog(Activity activity, boolean beforeConnection) {
        return new AlertDialog.Builder(activity)
                .setNegativeButton(android.R.string.cancel, (dialog, which) -> dialog.cancel())
                .setOnCancelListener(dialog -> { if (beforeConnection) cancelReconnect(activity); });
    }

    private static void cancelReconnect(Activity activity) {
        cancel(activity);
        if (activity instanceof com.limelight.Game) activity.finish();
    }

    public interface StreamSwitch { void apply(boolean endApplication, Runnable afterStopped); }

    private static boolean matchesSavedRoute(Activity activity, String host, String deviceId, boolean exclusive) {
        return deviceId.equalsIgnoreCase(preferences(activity).getString(key(host) + DEVICE_SUFFIX, "")) &&
                exclusive == usesPhysicalDisplayExclusively(activity, host);
    }

    public static void choose(Activity activity, String hostId, String hostAddress,
                              boolean beforeConnection, Selected selected) {
        chooseRequest(activity, hostId, hostAddress, beforeConnection, guard(activity, selected));
    }
    private static void chooseRequest(Activity activity, String hostId, String hostAddress,
                                      boolean beforeConnection, Selected selected) {
        loadChoices(activity, hostId, hostAddress, beforeConnection, selected, false, null);
    }

    public interface CaptureChanged { void move(int width, int height); }
    private static final java.util.Set<Activity> liveChanges = java.util.Collections.newSetFromMap(new java.util.WeakHashMap<>());

    public static void openStreamDisplays(Activity activity, String host, String address, String session,
            boolean quick, int dx, int dy, CaptureChanged captureChanged, StreamSwitch callback, Selected onReady) {
        if (liveChanges.contains(activity)) return;
        GuardedSelection selection = guard(activity, onReady);
        selection.address = address;
        selection.streamSwitch = callback;
        selection.session = session;
        selection.captureChanged = captureChanged;
        if (!quick) {
            showSessionPanel(activity, host, address, selection, null);
            return;
        }
        DesktopLayout.Direction direction = dx < 0 ? DesktopLayout.Direction.LEFT : dx > 0 ? DesktopLayout.Direction.RIGHT :
                dy < 0 ? DesktopLayout.Direction.UP : dy > 0 ? DesktopLayout.Direction.DOWN : null;
        loadChoices(activity, host, address, false, selection, true, direction);
    }

    private static void loadChoices(Activity activity, String hostId, String hostAddress, boolean beforeConnection,
            Selected selected, boolean quick, DesktopLayout.Direction direction) {
        AlertDialog loading = connectionDialog(activity, beforeConnection)
                .setTitle(quick ? R.string.quick_switch_screen : R.string.monitor_select_title)
                .setMessage(R.string.monitor_loading)
                .create();
        if (!quick) loading.show();
        new Thread(() -> {
            DisplayInventoryClient.Inventory inventory = null;
            try {
                inventory = quick ? DisplayInventoryClient.fetchCached(activity, hostAddress) :
                        DisplayInventoryClient.fetch(activity, hostAddress);
                LimeLog.info("Loaded " + inventory.displays.size() + " named displays from " + hostAddress);
            } catch (Exception exception) {
                // The UI below offers retry and never invents monitor rows.
                LimeLog.warning("Unable to load named displays from " + hostAddress + ": " +
                        exception.getClass().getSimpleName() + ": " + exception.getMessage());
            }
            DisplayInventoryClient.Inventory result = inventory;
            activity.runOnUiThread(() -> {
                if (!quick && !loading.isShowing()) return;
                if (!quick) loading.dismiss();
                if (activity.isFinishing() || activity.isDestroyed() || !isCurrent(activity, selected)) return;
                if (quick && (result == null || result.displays.isEmpty())) {
                    showSessionPanel(activity, hostId, hostAddress, (GuardedSelection)selected, null);
                } else if (result == null || result.displays.isEmpty()) {
                    showUnavailable(activity, hostId, hostAddress, beforeConnection, selected);
                } else if (quick) {
                    showQuickScreens(activity, hostId, hostAddress, selected, result, direction);
                } else {
                        showDisplays(activity, hostId, hostAddress, beforeConnection, selected, result.exclusive,
                                result.displays, result.virtualResolutions,
                                result.virtualRefreshRates);
                }
            });
        }, "PiuLink display inventory").start();
    }

    private static void showQuickScreens(Activity activity, String host, String address, Selected selected,
            DisplayInventoryClient.Inventory inventory, DesktopLayout.Direction direction) {
        List<DisplayInventoryClient.Display> screens = inventory.displays;
        GuardedSelection selection = (GuardedSelection)selected;
        if (!inventory.supportsSessionDisplays) {
            showSessionPanel(activity, host, address, selection, inventory);
            return;
        }
        int current = currentScreen(activity, host, screens);
        if (direction != null && current >= 0 && screens.get(current).bounds != null) {
            int next = DesktopLayout.neighbor(screens, current, direction);
            if (next >= 0) switchTo(activity, host, selection, screens.get(next));
            else android.widget.Toast.makeText(activity, R.string.no_screen_direction, android.widget.Toast.LENGTH_SHORT).show();
            return;
        }
        int other = DesktopLayout.onlyOtherActive(screens, current);
        if (direction == null && other >= 0) {
            switchTo(activity, host, selection, screens.get(other));
            return;
        }
        showSessionPanel(activity, host, address, selection, inventory);
    }

    private static int currentScreen(Activity activity, String host, List<DisplayInventoryClient.Display> screens) {
        String saved = preferences(activity).getString(key(host) + DEVICE_SUFFIX, "");
        int savedIndex = -1, primary = -1;
        for (int i = 0; i < screens.size(); i++) {
            DisplayInventoryClient.Display display = screens.get(i);
            if (display.selected) return i;
            if (display.deviceId.equalsIgnoreCase(saved)) savedIndex = i;
            if (display.primary) primary = i;
        }
        return savedIndex >= 0 ? savedIndex : primary;
    }

    private static void switchTo(Activity activity, String host, GuardedSelection selection, DisplayInventoryClient.Display target) {
        if (liveChanges.contains(activity)) return;
        liveChanges.add(activity);
        new Thread(() -> {
            String failure = null;
            try {
                DisplayInventoryClient.sessionDisplays(activity, selection.address, selection.session,
                        "capture", target.deviceId, -1, false);
            } catch (DisplayInventoryClient.SessionError error) { failure = error.reason; }
            catch (Exception error) { failure = "unavailable"; }
            String result = failure;
            activity.runOnUiThread(() -> {
                liveChanges.remove(activity);
                if (activity.isFinishing() || activity.isDestroyed() || !isCurrent(activity, selection)) return;
                if (result != null) {
                    int message = "invalid_request".equals(result) || "unsupported".equals(result)
                            ? R.string.screen_live_update : R.string.screen_live_failed;
                    android.widget.Toast.makeText(activity, message, android.widget.Toast.LENGTH_LONG).show();
                    return;
                }
                selection.captureChanged.move(target.width, target.height);
                preferences(activity).edit().putString(key(host) + DEVICE_SUFFIX, target.deviceId)
                        .putBoolean(key(host) + ":virtual", target.virtual)
                        .putBoolean(key(host) + EXCLUSIVE_SUFFIX, false).apply();
                android.widget.Toast.makeText(activity, target.name, android.widget.Toast.LENGTH_SHORT).show();
            });
        }, "PiuLink live capture").start();
    }

    private static void showSessionPanel(Activity activity, String host, String address, GuardedSelection selection,
            DisplayInventoryClient.Inventory initial) {
        SessionDisplayControls.show(activity, address, selection.session, initial, (panel, inventory) -> {
            int current = currentScreen(activity, host, inventory.displays);
            if (current >= 0) panel.addTarget(inventory.displays.get(current), true, null);
            for (int i = 0; i < inventory.displays.size(); i++) {
                if (i == current) continue;
                DisplayInventoryClient.Display display = inventory.displays.get(i);
                panel.addTarget(display, false, inventory.supportsSessionDisplays
                        ? () -> switchTo(activity, host, selection, display) : null);
            }
            if (current >= 0) showDisplays(activity, host, address, false, selection, inventory.exclusive,
                    java.util.Collections.singletonList(inventory.displays.get(current)),
                    inventory.virtualResolutions, inventory.virtualRefreshRates, panel);
        });
    }

    private static void showDisplays(Activity activity, String hostId, String hostAddress,
                                     boolean beforeConnection, Selected selected, boolean hostExclusive,
                                     List<DisplayInventoryClient.Display> displays,
                                     List<DisplayInventoryClient.Resolution> virtualResolutions,
                                     List<Integer> virtualRefreshRates) {
        showDisplays(activity, hostId, hostAddress, beforeConnection, selected, hostExclusive, displays,
                virtualResolutions, virtualRefreshRates, null);
    }

    private static void showDisplays(Activity activity, String hostId, String hostAddress,
                                     boolean beforeConnection, Selected selected, boolean hostExclusive,
                                     List<DisplayInventoryClient.Display> displays,
                                     List<DisplayInventoryClient.Resolution> virtualResolutions,
                                     List<Integer> virtualRefreshRates, SessionDisplayControls panel) {
        SharedPreferences preferences = preferences(activity);
        String savedDevice = preferences.getString(key(hostId) + DEVICE_SUFFIX, "");
        List<DisplayInventoryClient.Display> choices = new ArrayList<>();
        choices.addAll(displays);
        if (choices.isEmpty()) {
            showUnavailable(activity, hostId, hostAddress, beforeConnection, selected);
            return;
        }

        int selectedRow = -1;
        int configuredRow = -1;
        int primaryPhysicalRow = -1;
        String[] rows = new String[choices.size()];
        for (int i = 0; i < choices.size(); i++) {
            DisplayInventoryClient.Display display = choices.get(i);
            rows[i] = display.virtual ? formatMerge(activity, display) : format(activity, display);
            if (display.deviceId.equalsIgnoreCase(savedDevice)) selectedRow = i;
            if (display.selected) configuredRow = i;
            if (!display.virtual && display.primary) primaryPhysicalRow = i;
        }
        // A removed/reordered monitor must never silently turn into the first row, which is
        // normally the exclusive virtual desktop. Prefer the Host's current target, then the
        // Windows primary physical display, and only then the first available row.
        if (selectedRow < 0) selectedRow = configuredRow;
        if (selectedRow < 0) selectedRow = primaryPhysicalRow;
        if (selectedRow < 0) selectedRow = 0;
        final int[] choice = {selectedRow};
        android.widget.CheckBox gather = new android.widget.CheckBox(activity);
        gather.setText(R.string.monitor_gather_physical);
        gather.setChecked(gathersWindows(activity, hostId));
        int padding = (int) (16 * activity.getResources().getDisplayMetrics().density);
        gather.setPadding(0, padding / 2, 0, 0);

        android.widget.CheckBox physicalExclusive = new android.widget.CheckBox(activity);
        physicalExclusive.setText(R.string.monitor_physical_exclusive);
        DisplayInventoryClient.Display initiallySelected = choices.get(selectedRow);
        physicalExclusive.setChecked(!initiallySelected.virtual &&
                usesPhysicalDisplayExclusively(activity, hostId));
        physicalExclusive.setPadding(0, padding / 2, 0, 0);

        TextView summary = new TextView(activity);
        summary.setText(R.string.monitor_selection_summary);
        summary.setPadding(0, 0, 0, padding / 2);

        TextView virtualGatherHint = new TextView(activity);
        virtualGatherHint.setText(R.string.monitor_virtual_gather_hint);
        virtualGatherHint.setPadding(0, padding / 2, 0, 0);

        Button refreshButton = new Button(activity);
        refreshButton.setText(R.string.monitor_refresh);
        refreshButton.setAllCaps(false);

        // Some landscape Android builds collapse AlertDialog's positive action while
        // retaining the negative action. Keep the primary action inside the page so
        // it remains visible with every dialog-button policy.
        Button applyButton = new Button(activity);
        applyButton.setText(beforeConnection ? R.string.monitor_connect : R.string.monitor_apply_reconnect);
        applyButton.setAllCaps(false);
        applyButton.setMinHeight(Math.round(52 * activity.getResources().getDisplayMetrics().density));

        ListView displayList = new ListView(activity);
        displayList.setChoiceMode(ListView.CHOICE_MODE_SINGLE);
        displayList.setSelector(R.drawable.bg_dialog_choice);
        displayList.setAdapter(new ArrayAdapter<>(activity,
                android.R.layout.simple_list_item_single_choice, rows));
        displayList.setItemChecked(selectedRow, true);

        LinearLayout virtualOptions = new LinearLayout(activity);
        virtualOptions.setOrientation(LinearLayout.VERTICAL);
        virtualOptions.setPadding(padding, 0, 0, 0);
        TextView scaleLabel = new TextView(activity);
        scaleLabel.setText(R.string.monitor_scale_label);
        scaleLabel.setPadding(0, padding / 2, 0, 0);
        Spinner scaleSpinner = new Spinner(activity);
        scaleSpinner.setBackgroundResource(R.drawable.bg_dialog_choice);
        TextView scaleHint = new TextView(activity);
        scaleHint.setText(R.string.monitor_scale_summary);
        scaleHint.setPadding(0, padding / 3, 0, 0);
        virtualOptions.addView(scaleLabel);
        virtualOptions.addView(scaleSpinner, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        virtualOptions.addView(scaleHint);

        LinearLayout selectors = new LinearLayout(activity);
        android.util.DisplayMetrics windowMetrics = activity.getResources().getDisplayMetrics();
        boolean compactWidth = panel != null || windowMetrics.widthPixels / windowMetrics.density < 600;
        selectors.setOrientation(compactWidth ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL);
        int displayListHeight = Math.max(Math.round((compactWidth ? 176 : 220) * windowMetrics.density),
                Math.min(Math.round((compactWidth ? 300 : 420) * windowMetrics.density),
                        Math.round(windowMetrics.heightPixels * (compactWidth ? 0.34f : 0.48f))));
        int optionsWidth = Math.max(Math.round(180 * windowMetrics.density),
                Math.min(Math.round(300 * windowMetrics.density),
                        Math.round(windowMetrics.widthPixels * 0.28f)));
        if (compactWidth) {
            selectors.addView(displayList, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, displayListHeight));
            virtualOptions.setPadding(0, padding / 2, 0, 0);
            selectors.addView(virtualOptions, new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        }
        else {
            selectors.addView(displayList, new LinearLayout.LayoutParams(0, displayListHeight, 1f));
            selectors.addView(virtualOptions, new LinearLayout.LayoutParams(
                    optionsWidth, ViewGroup.LayoutParams.WRAP_CONTENT));
        }

        LinearLayout content = new LinearLayout(activity);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(padding, 0, padding, 0);
        content.addView(summary);
        content.addView(refreshButton);
        content.addView(selectors);
        content.addView(virtualGatherHint);
        content.addView(physicalExclusive);
        content.addView(gather);
        content.addView(applyButton, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        Runnable updateOptions = () -> {
            DisplayInventoryClient.Display display = choices.get(choice[0]);
            boolean exclusive = physicalExclusive.isChecked();
            virtualOptions.setVisibility(display.virtual ? View.GONE : View.VISIBLE);
            virtualGatherHint.setVisibility(display.virtual ? View.VISIBLE : View.GONE);
            physicalExclusive.setVisibility(display.virtual ? View.GONE : View.VISIBLE);
            gather.setVisibility(!display.virtual && !exclusive ? View.VISIBLE : View.GONE);
            boolean canScale = !display.scaleOptions.isEmpty();
            scaleLabel.setVisibility(canScale ? View.VISIBLE : View.GONE);
            scaleSpinner.setVisibility(canScale ? View.VISIBLE : View.GONE);
            scaleHint.setVisibility(canScale ? View.VISIBLE : View.GONE);
            if (canScale && !display.deviceId.equals(scaleSpinner.getTag())) {
                configureScaleSpinner(activity, preferences, hostId, display, scaleSpinner);
                scaleSpinner.setTag(display.deviceId);
            }
        };
        displayList.setOnItemClickListener((parent, view, position, id) -> {
            choice[0] = position;
            displayList.setItemChecked(position, true);
            updateOptions.run();
        });
        physicalExclusive.setOnCheckedChangeListener((button, checked) -> updateOptions.run());
        updateOptions.run();

        final AlertDialog[] dialogReference = new AlertDialog[1];
        applyButton.setOnClickListener(view -> {
            DisplayInventoryClient.Display display = choices.get(choice[0]);
            if (display.virtual) {
                if (dialogReference[0] != null) dialogReference[0].dismiss();
                showVirtualResolution(activity, hostId, hostAddress, selected, display,
                    virtualResolutions, virtualRefreshRates, beforeConnection); return;
            }
            boolean exclusive = physicalExclusive.isChecked();
            if (selected instanceof GuardedSelection)
                ((GuardedSelection) selected).gatherDraft = gather.isChecked();
            DisplayInventoryClient.Mode mode = physicalMode(activity, hostAddress, display);
            int rate = mode.fps;
            int scalePercent = selectedScale(choices.get(choice[0]), scaleSpinner);
            if (dialogReference[0] != null) dialogReference[0].dismiss();
            if (beforeConnection) prepareAndAccept(activity, hostId, hostAddress,
                    selected, display, exclusive, mode.width, mode.height, rate, scalePercent);
            else saveAndAccept(activity, hostId, selected, display,
                    exclusive, mode.width, mode.height, rate, scalePercent);
        });

        if (panel != null) {
            refreshButton.setVisibility(View.GONE);
            summary.setVisibility(View.GONE);
            selectors.removeView(virtualOptions);
            selectors.setVisibility(View.GONE);
            content.addView(virtualOptions, 0);
            content.setPadding(0, padding / 2, 0, padding / 2);
            scaleHint.setVisibility(View.GONE);
            applyButton.setText(R.string.screen_apply_options);
            panel.styleAction(applyButton, R.drawable.ic_computer);
            dialogReference[0] = panel.dialog;
            panel.setOptions(content);
            return;
        }
        ScrollView scrollContent = new ScrollView(activity);
        scrollContent.setFillViewport(true);
        scrollContent.addView(content);
        AlertDialog alertDialog = connectionDialog(activity, beforeConnection)
                .setTitle(R.string.monitor_select_title)
                .setView(scrollContent)
                .create();
        dialogReference[0] = alertDialog;
        refreshButton.setOnClickListener(view -> {
            alertDialog.dismiss();
            chooseRequest(activity, hostId, hostAddress, beforeConnection, selected);
        });
        installTvDialogNavigation(alertDialog, displayList, -1);
        alertDialog.show();
    }

    private static List<DisplayInventoryClient.Mode> supportedModes(Activity activity, DisplayInventoryClient.Display display) {
        float maximum = activity.getWindowManager().getDefaultDisplay().getRefreshRate();
        for (Display.Mode mode : activity.getWindowManager().getDefaultDisplay().getSupportedModes())
            maximum = Math.max(maximum, mode.getRefreshRate());
        List<DisplayInventoryClient.Mode> result = new ArrayList<>();
        for (DisplayInventoryClient.Mode mode : display.modes)
            if (mode.fps <= Math.ceil(maximum)) result.add(mode);
        return result;
    }

    private static DisplayInventoryClient.Mode preferredMode(Activity activity, String host, List<DisplayInventoryClient.Mode> modes) {
        String resolution = preferences(activity).getString(key(host) + RESOLUTION_SUFFIX, "1920x1080");
        int fps = preferences(activity).getInt(key(host) + REFRESH_RATE_SUFFIX, 60);
        for (DisplayInventoryClient.Mode mode : modes) if (mode.resolution().equals(resolution) && mode.fps == fps) return mode;
        for (DisplayInventoryClient.Mode mode : modes) if (mode.resolution().equals("1920x1080") && mode.fps == 60) return mode;
        return modes.isEmpty() ? null : modes.get(0);
    }

    private static void showVirtualResolution(Activity activity, String hostId, String hostAddress,
            Selected selected, DisplayInventoryClient.Display display,
            List<DisplayInventoryClient.Resolution> ignoredSizes, List<Integer> ignoredRates, boolean beforeConnection) {
        List<DisplayInventoryClient.Mode> modes = supportedModes(activity, display);
        if (modes.isEmpty()) {
            new AlertDialog.Builder(activity).setTitle(R.string.monitor_resolution_title)
                .setMessage(R.string.monitor_resolution_unavailable).setPositiveButton(android.R.string.ok, null).show();
            return;
        }
        final int[] chosen = {modes.indexOf(preferredMode(activity, hostId, modes))};
        String[] labels = new String[modes.size()];
        for (int index = 0; index < modes.size(); index++) labels[index] = modes.get(index).toString();

        int padding = Math.round(16 * activity.getResources().getDisplayMetrics().density);
        ListView modeList = new ListView(activity);
        modeList.setChoiceMode(ListView.CHOICE_MODE_SINGLE);
        modeList.setSelector(R.drawable.bg_dialog_choice);
        modeList.setAdapter(new ArrayAdapter<>(activity,
                android.R.layout.simple_list_item_single_choice, labels));
        modeList.setItemChecked(chosen[0], true);
        modeList.setSelection(chosen[0]);
        modeList.setOnItemClickListener((parent, view, position, id) -> {
            chosen[0] = position;
            modeList.setItemChecked(position, true);
        });

        Spinner scaleChoice = new Spinner(activity);
        scaleChoice.setBackgroundResource(R.drawable.bg_dialog_choice);
        configureScaleSpinner(activity, preferences(activity), hostId, display, scaleChoice);

        TextView scaleLabel = new TextView(activity);
        scaleLabel.setText(R.string.monitor_scale_label);
        scaleLabel.setPadding(0, padding / 2, 0, 0);
        TextView scaleHint = new TextView(activity);
        scaleHint.setText(R.string.monitor_scale_summary);
        scaleHint.setPadding(0, padding / 3, 0, 0);
        boolean canScale = !display.scaleOptions.isEmpty();
        scaleLabel.setVisibility(canScale ? View.VISIBLE : View.GONE);
        scaleChoice.setVisibility(canScale ? View.VISIBLE : View.GONE);
        scaleHint.setVisibility(canScale ? View.VISIBLE : View.GONE);

        Button applyButton = new Button(activity);
        applyButton.setText(beforeConnection ? R.string.monitor_connect : R.string.monitor_apply_reconnect);
        applyButton.setAllCaps(false);
        applyButton.setMinHeight(Math.round(52 * activity.getResources().getDisplayMetrics().density));

        LinearLayout content = new LinearLayout(activity);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(padding, 0, padding, 0);
        int listHeight = Math.max(Math.round(180 * activity.getResources().getDisplayMetrics().density),
                Math.min(Math.round(360 * activity.getResources().getDisplayMetrics().density),
                        Math.round(activity.getResources().getDisplayMetrics().heightPixels * 0.42f)));
        content.addView(modeList, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, listHeight));
        content.addView(scaleLabel);
        content.addView(scaleChoice, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        content.addView(scaleHint);
        content.addView(applyButton, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        ScrollView scrollContent = new ScrollView(activity);
        scrollContent.setFillViewport(true);
        scrollContent.addView(content, new ScrollView.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        AlertDialog dialog = connectionDialog(activity, beforeConnection)
                .setTitle(display.name)
                .setView(scrollContent)
                .create();
        applyButton.setOnClickListener(view -> {
            DisplayInventoryClient.Mode mode = modes.get(chosen[0]);
            int scale = selectedScale(display, scaleChoice);
            dialog.dismiss();
            if (beforeConnection) prepareAndAccept(activity, hostId, hostAddress, selected,
                    display, true, mode.width, mode.height, mode.fps, scale);
            else saveAndAccept(activity, hostId, selected, display, true,
                    mode.width, mode.height, mode.fps, scale);
        });
        installTvDialogNavigation(dialog, modeList, chosen[0]);
        dialog.show();
    }
    /** Makes system dialogs predictable and visibly navigable with TV remotes and gamepads. */
    private static void installTvDialogNavigation(AlertDialog dialog, View initialFocus,
                                                  int selectedListRow) {
        final long[] suppressFallbackUntil = {0};
        dialog.setOnKeyListener((ignored, keyCode, event) -> {
            long now = SystemClock.uptimeMillis();
            if ((keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_BACK) &&
                    now < suppressFallbackUntil[0]) {
                return true;
            }
            if (keyCode == KeyEvent.KEYCODE_BUTTON_A) {
                if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                    View focused = dialog.getCurrentFocus();
                    if (focused instanceof ListView) {
                        ListView list = (ListView) focused;
                        int position = list.getSelectedItemPosition();
                        int childIndex = position - list.getFirstVisiblePosition();
                        View row = childIndex >= 0 && childIndex < list.getChildCount()
                                ? list.getChildAt(childIndex) : null;
                        if (position >= 0) list.performItemClick(row, position,
                                list.getAdapter().getItemId(position));
                    }
                    else if (focused != null) focused.performClick();
                    suppressFallbackUntil[0] = now + 750;
                }
                return true;
            }
            if (keyCode == KeyEvent.KEYCODE_BUTTON_B) {
                if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                    dialog.cancel();
                    suppressFallbackUntil[0] = now + 750;
                }
                return true;
            }
            return false;
        });
        dialog.setOnShowListener(ignored -> {
            ListView list = dialog.getListView();
            if (list != null) {
                list.setSelector(R.drawable.bg_dialog_choice);
                list.setDrawSelectorOnTop(false);
                if (selectedListRow >= 0) list.setSelection(selectedListRow);
                list.setOnKeyListener((view, keyCode, event) -> {
                    if (event.getAction() == KeyEvent.ACTION_DOWN &&
                            keyCode == KeyEvent.KEYCODE_DPAD_DOWN &&
                            list.getSelectedItemPosition() >= list.getCount() - 1) {
                        Button positive = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
                        return positive != null && positive.requestFocus();
                    }
                    return false;
                });
            }
            int[] buttonIds = {AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE,
                    AlertDialog.BUTTON_NEUTRAL};
            for (int buttonId : buttonIds) {
                Button button = dialog.getButton(buttonId);
                if (button != null) {
                    button.setFocusable(true);
                    button.setBackgroundResource(R.drawable.bg_dialog_choice);
                }
            }
            View target = initialFocus != null ? initialFocus : list;
            if (target != null) target.post(target::requestFocus);
        });
    }

    private static void showUnavailable(Activity activity, String hostId, String hostAddress,
                                        boolean beforeConnection, Selected selected) {
        AlertDialog.Builder builder = connectionDialog(activity, beforeConnection)
                .setTitle(R.string.monitor_select_title)
                .setMessage(R.string.monitor_unavailable)
                .setPositiveButton(R.string.monitor_refresh,
                        (dialog, which) -> chooseRequest(activity, hostId, hostAddress, beforeConnection, selected));
        builder.show();
    }

    private static void prepareAndAccept(Activity activity, String hostId, String hostAddress,
                                         Selected selected, DisplayInventoryClient.Display display,
                                         boolean exclusive, int width, int height) {
        prepareAndAccept(activity, hostId, hostAddress, selected, display,
                exclusive, width, height, 0);
    }

    private static void prepareAndAccept(Activity activity, String hostId, String hostAddress,
                                         Selected selected, DisplayInventoryClient.Display display,
                                         boolean exclusive, int width, int height, int fps) {
        prepareAndAccept(activity, hostId, hostAddress, selected, display,
                exclusive, width, height, fps, 0);
    }

    private static void prepareAndAccept(Activity activity, String hostId, String hostAddress,
                                         Selected selected, DisplayInventoryClient.Display display,
                                         boolean exclusive, int width, int height, int fps,
                                         int scalePercent) {
        final DisplayPreparationState lease;
        synchronized (MonitorSelection.class) {
            if (!isCurrent(activity, selected)) return;
            lease = preparations.start(activity, hostAddress);
        }
        GuardedSelection guarded = (GuardedSelection) selected;
        final AlertDialog preparing;
        if (guarded.progress != null && guarded.progress.isShowing()) {
            preparing = guarded.progress;
            preparing.setMessage(activity.getString(R.string.monitor_preparing));
        } else {
            preparing = new AlertDialog.Builder(activity).setTitle(R.string.screen_connecting)
                    .setMessage(R.string.monitor_preparing)
                    .setNegativeButton(android.R.string.cancel, (d, w) -> cancelReconnect(activity)).create();
            preparing.setOnCancelListener(d -> cancelReconnect(activity));
            preparing.show();
        }
        guarded.progress = null;
        new Thread(() -> {
            long preparationStarted = android.os.SystemClock.elapsedRealtime();
            Exception failure = null;
            try {
                if (!isCurrent(activity, selected)) throw new java.io.IOException("Display request cancelled");
                if (display != null && !display.virtual && !exclusive && desiredGather(activity, hostId, selected) &&
                        !DisplayInventoryClient.fetchCached(activity, hostAddress).supportsWindowGathering)
                    throw new java.io.IOException("请更新 Host，以使用集中窗口功能。");
                int shownWaitReason = 0;
                for (int attempt = 0; ; attempt++) {
                    if (!isCurrent(activity, selected)) throw new java.io.IOException("Display request cancelled");
                    try {
                        // Resolution and FPS always reach the Android stream. Only a virtual
                        // display accepts a Host-side desktop mode; physical displays keep
                        // their Windows mode even when they are the only active display.
                        DisplayInventoryClient.select(activity, hostAddress,
                                display == null ? null : display.deviceId, exclusive,
                                display != null && display.virtual && exclusive ? width : 0,
                                display != null && display.virtual && exclusive ? height : 0,
                                display != null && !display.virtual && !exclusive &&
                                        desiredGather(activity, hostId, selected), scalePercent, fps, lease.requestId);
                        if (!isCurrent(activity, selected)) {
                            release(activity, hostAddress, lease.requestId);
                            throw new java.io.IOException("Display request cancelled");
                        }
                        break;
                    } catch (DisplayInventoryClient.StreamActiveException busy) {
                        int reason = busy.recovering ? R.string.screen_wait_restore : R.string.screen_wait_active;
                        if (reason != shownWaitReason) {
                            shownWaitReason = reason;
                            activity.runOnUiThread(() -> {
                                if (preparing.isShowing() && isCurrent(activity, selected))
                                    preparing.setMessage(activity.getString(reason));
                            });
                        }
                        if (attempt >= 39) throw busy;
                        Thread.sleep(500);
                    }
                }
                LimeLog.info("PiuLink display prepared in " +
                        (android.os.SystemClock.elapsedRealtime() - preparationStarted) + " ms");
            } catch (Exception exception) {
                if (!(exception instanceof DisplayInventoryClient.StreamActiveException))
                    DisplayInventoryClient.invalidate(hostAddress);
                failure = exception;
            }
            Exception result = failure;
            activity.runOnUiThread(() -> {
                if (!activity.isDestroyed() && preparing.isShowing()) {
                    preparing.dismiss();
                }
                synchronized (MonitorSelection.class) {
                    if (!preparations.handOff(activity, lease)) return;
                }
                if (activity.isFinishing() || activity.isDestroyed() || !isCurrent(activity, selected) || result != null)
                    release(activity, hostAddress, lease.requestId);
                if (activity.isFinishing() || activity.isDestroyed() || !isCurrent(activity, selected)) return;
                if (result == null) {
                    ((GuardedSelection) selected).preparedId = lease.requestId;
                    saveAndAccept(activity, hostId, selected, display, exclusive,
                            width, height, fps, scalePercent);
                } else {
                    LimeLog.warning("Unable to prepare display: " + result.getMessage());
                    connectionDialog(activity, true)
                            .setTitle(R.string.monitor_prepare_failed_title)
                            .setMessage(preparationError(activity, result))
                            .setPositiveButton(result instanceof DisplayInventoryClient.StreamActiveException ?
                                            R.string.screen_retry_connection : R.string.monitor_refresh, (dialog, which) -> {
                                DisplayInventoryClient.invalidate(hostAddress);
                                if (result instanceof DisplayInventoryClient.StreamActiveException) {
                                    prepareAndAccept(activity, hostId, hostAddress, selected, display, exclusive,
                                            width, height, fps, scalePercent);
                                } else {
                                    chooseRequest(activity, hostId, hostAddress, true, selected);
                                }
                            })
                            .show();
                }
            });
        }, "PiuLink display preparation").start();
    }

    private static String format(Activity activity, DisplayInventoryClient.Display display) {
        String kind = display.virtual
                ? activity.getString(R.string.monitor_virtual)
                : activity.getString(R.string.monitor_physical);
        String badges = display.primary
                ? kind + " · " + activity.getString(R.string.monitor_primary)
                : kind;
        if (display.selected) badges += " · " + activity.getString(R.string.monitor_current);
        if (display.width > 0 && display.height > 0) {
            return activity.getString(R.string.monitor_named_with_resolution,
                    display.name, display.width, display.height, badges);
        }
        return activity.getString(R.string.monitor_named, display.name, badges);
    }
    private static String preparationError(Activity activity, Exception error) {
        if (error instanceof DisplayInventoryClient.StreamActiveException)
            return activity.getString(((DisplayInventoryClient.StreamActiveException) error).recovering ?
                    R.string.screen_restore_timeout : R.string.screen_active_timeout);
        if (error instanceof java.net.SocketTimeoutException)
            return "准备屏幕超时。请确认电脑在线、网络可达，然后重试。未开始串流的屏幕改动会由 Host 恢复。";
        if (error instanceof java.net.ConnectException || error instanceof java.net.UnknownHostException)
            return "无法连接电脑的屏幕服务。请打开 PiuLink Host，并检查网络或 Tailscale 连接。";
        if (error.getMessage() != null && error.getMessage().contains("请更新 Host")) return error.getMessage();
        return activity.getString(R.string.monitor_prepare_failed) + "\n\n" +
                (error.getMessage() == null ? "电脑未确认屏幕就绪，请检查 Host 的显示器状态。" : error.getMessage());
    }

    private static String formatMerge(Activity activity, DisplayInventoryClient.Display display) {
        String current = display.selected
                ? " · " + activity.getString(R.string.monitor_current)
                : "";
        return activity.getString(R.string.monitor_merge_windows, display.name, current);
    }

    private static void saveAndAccept(Activity activity, String host, Selected selected,
                                      DisplayInventoryClient.Display display) {
        saveAndAccept(activity, host, selected, display, false, 0, 0, 0);
    }

    private static void saveAndAccept(Activity activity, String host, Selected selected,
                                      DisplayInventoryClient.Display display,
                                      boolean exclusive, int width, int height, int fps) {
        saveAndAccept(activity, host, selected, display, exclusive, width, height, fps, 0);
    }

    private static void saveAndAccept(Activity activity, String host, Selected selected,
                                      DisplayInventoryClient.Display display,
                                      boolean exclusive, int width, int height, int fps,
                                      int scalePercent) {
        if (activity.isFinishing() || activity.isDestroyed() || !isCurrent(activity, selected)) return;
        GuardedSelection guarded = (GuardedSelection) selected;
        if (guarded.streamSwitch != null && display != null) {
            boolean routeChanged = !matchesSavedRoute(activity, host, display.deviceId, exclusive);
            boolean sameMode = !display.virtual ||
                    ((width + "x" + height).equals(preferences(activity).getString(key(host) + RESOLUTION_SUFFIX, "")) &&
                     fps == preferences(activity).getInt(key(host) + REFRESH_RATE_SUFFIX, 60));
            boolean sameGather = display.virtual || exclusive || desiredGather(activity, host, selected) == gathersWindows(activity, host);
            boolean wasExclusive = preferences(activity).getBoolean(key(host) + EXCLUSIVE_SUFFIX, false);
            if (routeChanged && !wasExclusive && display.isActive() && !display.virtual && !exclusive &&
                    !desiredGather(activity, host, selected) && (scalePercent == 0 || scalePercent == display.scalePercent)) {
                switchTo(activity, host, guarded, display);
                return;
            }
            if (!routeChanged && sameMode && sameGather && scalePercent == preferredScale(activity, host, display) &&
                    (scalePercent == 0 || scalePercent == display.scalePercent)) {
                android.widget.Toast.makeText(activity, R.string.screen_unchanged, android.widget.Toast.LENGTH_SHORT).show();
                return;
            }
            Runnable change = () -> {
                if (!isCurrent(activity, selected)) return;
                StreamSwitch switcher = guarded.streamSwitch;
                guarded.streamSwitch = null;
                switcher.apply(routeChanged, () -> {
                    if (isCurrent(activity, selected) && !activity.isFinishing() && !activity.isDestroyed())
                        prepareAndAccept(activity, host, guarded.address, selected, display, exclusive, width, height, fps, scalePercent);
                });
            };
            if (routeChanged) {
                new AlertDialog.Builder(activity).setMessage(R.string.monitor_confirm_end_application)
                        .setNegativeButton(android.R.string.cancel, null)
                        .setPositiveButton(R.string.monitor_end_and_switch, (d, w) -> change.run()).show();
            } else change.run();
            return;
        }
        SharedPreferences.Editor editor = preferences(activity).edit();
        editor.putBoolean(key(host) + ":gather_windows", desiredGather(activity, host, selected));
        String preparedId = selected instanceof GuardedSelection ? ((GuardedSelection) selected).preparedId : null;
        if (display != null && display.virtual && width > 0 && height > 0 && fps > 0) {
            editor.putString(key(host) + RESOLUTION_SUFFIX, width + "x" + height)
                    .putInt(key(host) + REFRESH_RATE_SUFFIX, fps);
        }
        if (display == null) {
            editor.remove(key(host)).remove(key(host) + DEVICE_SUFFIX).remove(key(host) + ":virtual")
                    .remove(key(host) + ":label").remove(key(host) + ":exclusive");
            editor.apply();
            selected.accept(-1, 0, 0, 0, preparedId);
        } else {
            if (scalePercent > 0) {
                editor.putInt(scaleKey(host, display.deviceId), scalePercent);
            } else {
                editor.remove(scaleKey(host, display.deviceId));
            }
            editor.remove(key(host)).remove(key(host) + ":label")
                    .putString(key(host) + DEVICE_SUFFIX, display.deviceId)
                    .putBoolean(key(host) + ":virtual", display.virtual)
                    .putBoolean(key(host) + EXCLUSIVE_SUFFIX, exclusive)
                    .apply();
            selected.accept(display.captureIndex, width, height, fps, preparedId);
        }
    }

    private static SharedPreferences preferences(Activity activity) {
        return activity.getSharedPreferences(PREFERENCES, 0);
    }

    private static String key(String host) { return host == null ? "" : host; }
    private static String scaleKey(String host, String deviceId) {
        return key(host) + SCALE_SUFFIX + deviceId;
    }

    private static int preferredScale(Activity activity, String host,
                                      DisplayInventoryClient.Display display) {
        int saved = preferences(activity).getInt(scaleKey(host, display.deviceId), 0);
        return display.scaleOptions.contains(saved) ? saved : 0;
    }

    private static void configureScaleSpinner(Activity activity, SharedPreferences preferences,
                                              String host, DisplayInventoryClient.Display display,
                                              Spinner spinner) {
        List<String> rows = new ArrayList<>();
        rows.add(display.scalePercent > 0
                ? activity.getString(R.string.monitor_scale_keep_current_value, display.scalePercent)
                : activity.getString(R.string.monitor_scale_keep_current));
        for (int option : display.scaleOptions)
            rows.add(activity.getString(R.string.monitor_scale_value, option));
        ArrayAdapter<String> adapter = new ArrayAdapter<>(activity,
                android.R.layout.simple_spinner_item, rows);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spinner.setAdapter(adapter);
        int saved = preferences.getInt(scaleKey(host, display.deviceId), 0);
        int selected = display.scaleOptions.indexOf(saved);
        spinner.setSelection(selected < 0 ? 0 : selected + 1);
    }

    private static int selectedScale(DisplayInventoryClient.Display display, Spinner spinner) {
        int position = spinner.getSelectedItemPosition();
        return position <= 0 || position > display.scaleOptions.size()
                ? 0 : display.scaleOptions.get(position - 1);
    }
}
