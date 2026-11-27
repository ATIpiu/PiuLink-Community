package com.limelight;

import java.io.IOException;
import java.io.StringReader;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;

import com.limelight.computers.ComputerManagerListener;
import com.limelight.computers.ComputerManagerService;
import com.limelight.grid.AppGridAdapter;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.ui.AdapterFragment;
import com.limelight.ui.AdapterFragmentCallbacks;
import com.limelight.utils.CacheHelper;
import com.limelight.utils.Dialog;
import com.limelight.utils.ServerHelper;
import com.limelight.utils.ShortcutHelper;
import com.limelight.utils.SpinnerDialog;
import com.limelight.utils.UiHelper;

import android.app.Activity;
import android.app.Service;
import android.content.ComponentName;
import android.content.Context;
import com.limelight.piulink.transfer.FileTransferClient;
import com.limelight.piulink.ssh.TransferActivity;
import com.limelight.piulink.transfer.TransferHostResolver;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.SharedPreferences;
import android.content.res.Configuration;
import android.graphics.Bitmap;
import android.graphics.drawable.BitmapDrawable;
import android.graphics.drawable.Drawable;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.ContextMenu;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.ContextMenu.ContextMenuInfo;
import android.widget.AbsListView;
import android.widget.AdapterView;
import android.widget.AdapterView.OnItemClickListener;
import android.widget.ImageButton;
import android.widget.ImageView;
import android.widget.EditText;
import android.widget.TextView;
import android.widget.Toast;
import android.widget.AdapterView.AdapterContextMenuInfo;

import org.xmlpull.v1.XmlPullParserException;

public class AppView extends Activity implements AdapterFragmentCallbacks {
    private AppGridAdapter appGridAdapter;
    private String uuidString;
    private String computerName;
    private ShortcutHelper shortcutHelper;

    private ComputerDetails computer;
    private ComputerManagerService.ApplistPoller poller;
    private SpinnerDialog blockingLoadSpinner;
    private String lastRawApplist;
    static final String SHORTCUT_RESUME_ID = "ShortcutResumeAppId";
    static final String TV_CONTINUE_ID = "TvContinueAppId";
    private int pendingTvFocusId;
    static final String OPEN_DESKTOP = "OpenPublishedDesktop";
    static final String DESKTOP_TAP_TIME = "DesktopTapElapsedRealtime";

    private void timeDesktopEntry(String stage) {
        long tap = getIntent().getLongExtra(DESKTOP_TAP_TIME, 0);
        if (desktopRequested() && tap > 0)
            LimeLog.info("Desktop entry " + stage + " at " + (android.os.SystemClock.elapsedRealtime() - tap) + " ms");
    }
    private boolean shortcutHandled;
    private int lastRunningAppId;
    private boolean suspendGridUpdates;
    private boolean inForeground;
    private boolean showHiddenApps;
    private HashSet<Integer> hiddenAppIds = new HashSet<>();
    private HashSet<Integer> favoriteAppIds = new HashSet<>();
    private ArrayList<Integer> recentAppIds = new ArrayList<>();
    private int libraryFilterMode = AppGridAdapter.FILTER_ALL;
    private String librarySearchQuery = "";
    private boolean libraryToolsOpen;
    private android.window.OnBackInvokedCallback searchBack;
    private boolean libraryLoaded;
    private boolean checkingFileAccess;
    private int fileAccessState = R.string.transfer_checking;
    private static final int PICK_FILES = 48013;
    private static final int SEND_FILES = 48014;
    private View runningAppBanner;
    private TextView runningAppName;
    private View appEmptyState;
    private TextView appEmptyTitle;
    private TextView appEmptyMessage;
    private View filterAllButton;
    private View filterFavoritesButton;
    private View filterRecentButton;

    private final static int START_OR_RESUME_ID = 1;
    private final static int QUIT_ID = 2;
    private final static int START_WITH_QUIT = 4;
    private final static int VIEW_DETAILS_ID = 5;
    private final static int CREATE_SHORTCUT_ID = 6;
    private final static int HIDE_APP_ID = 7;
    private final static int FAVORITE_APP_ID = 8;

    public final static String HIDDEN_APPS_PREF_FILENAME = "HiddenApps";
    private final static String FAVORITE_APPS_PREF_FILENAME = "FavoriteApps";
    private final static String RECENT_APPS_PREF_FILENAME = "RecentApps";
    private final static int MAX_RECENT_APPS = 12;

    public final static String NAME_EXTRA = "Name";
    public final static String UUID_EXTRA = "UUID";
    public final static String NEW_PAIR_EXTRA = "NewPair";
    public final static String SHOW_HIDDEN_APPS_EXTRA = "ShowHiddenApps";

    private boolean serviceBound;
    private ComputerManagerService.ComputerManagerBinder connectedBinder;
    private ComputerManagerService.ComputerManagerBinder managerBinder;
    private final ServiceConnection serviceConnection = new ServiceConnection() {
        public void onServiceConnected(ComponentName className, IBinder binder) {
            timeDesktopEntry("service bound");
            final ComputerManagerService.ComputerManagerBinder localBinder =
                    ((ComputerManagerService.ComputerManagerBinder)binder);

            connectedBinder = localBinder;
            new Thread(() -> {
                localBinder.waitForReady();
                ComputerDetails saved = localBinder.getComputer(uuidString);
                if (saved == null) {
                    runOnUiThread(() -> { if (connectedBinder == localBinder) finish(); });
                    return;
                }
                shortcutHelper.createAppViewShortcut(saved, true, getIntent().getBooleanExtra(NEW_PAIR_EXTRA, false));
                shortcutHelper.reportComputerShortcutUsed(saved);
                String raw = null;
                List<NvApp> parsed = null;
                try {
                    raw = CacheHelper.readInputStreamToString(CacheHelper.openCacheFileForInput(getCacheDir(), "applist", uuidString));
                    parsed = NvHTTP.getAppListByReader(new StringReader(raw));
                } catch (IOException | XmlPullParserException unavailable) {
                    LimeLog.info("Loading app list from Host");
                }
                final String cachedXml = raw;
                final List<NvApp> cachedApps = parsed;
                runOnUiThread(() -> {
                    if (connectedBinder != localBinder || isFinishing() || isDestroyed()) return;
                    computer = saved;
                    checkFileAccess();
                    if (appGridAdapter != null) appGridAdapter.close();
                    appGridAdapter = new AppGridAdapter(AppView.this, PreferenceConfiguration.readPreferences(AppView.this),
                            saved, localBinder.getUniqueId(), showHiddenApps);
                    appGridAdapter.updateHiddenApps(hiddenAppIds, true);
                    appGridAdapter.updateLibraryState(favoriteAppIds, recentAppIds, libraryFilterMode, librarySearchQuery);
                    managerBinder = localBinder;
                    timeDesktopEntry("library initialized");
                    if (cachedApps != null) {
                        lastRawApplist = cachedXml;
                        updateUiWithAppList(cachedApps);
                    } else if (inForeground) {
                        loadAppsBlocking();
                    }
                    getFragmentManager().beginTransaction()
                            .replace(R.id.appFragmentContainer, new AdapterFragment()).commitAllowingStateLoss();
                    startComputerUpdates();
                });
            }, "PiuLink load library").start();
        }

        public void onServiceDisconnected(ComponentName className) {
            connectedBinder = null;
            if (poller != null) { poller.stop(); poller = null; }
            managerBinder = null;
        }
    };

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);

        // Reinflate the activity chrome so portrait and landscape controls can adapt.
        setContentView(R.layout.activity_app_view);
        UiHelper.notifyNewRootView(this);
        initializeLibraryViews();

        // If appGridAdapter is initialized, let it know about the configuration change.
        // If not, it will pick it up when it initializes.
        if (appGridAdapter != null) {
            // Update the app grid adapter to create grid items with the correct layout
            appGridAdapter.updateLayoutWithPreferences(this, PreferenceConfiguration.readPreferences(this));

            try {
                // Reinflate the app grid itself to pick up the layout change
                getFragmentManager().beginTransaction()
                        .replace(R.id.appFragmentContainer, new AdapterFragment())
                        .commitAllowingStateLoss();
            } catch (IllegalStateException e) {
                e.printStackTrace();
            }
        }
    }

    private void startComputerUpdates() {
        // Don't start polling if we're not bound or in the foreground
        if (managerBinder == null || !inForeground) {
            return;
        }

        if (computer.state == ComputerDetails.State.ONLINE && computer.pairState == PairingManager.PairState.PAIRED) {
            lastRunningAppId = computer.runningGameId;
            updateUiWithServerinfo(computer);
            handleShortcut(computer);
        }
        managerBinder.startAppListPolling(new ComputerManagerListener() {
            @Override public void notifyAppListUpdated(ComputerDetails details, List<NvApp> apps, Integer runningApp) {
                notifyComputerUpdated(details);
                runOnUiThread(() -> openPublishedDesktop(details, apps, runningApp));
            }

            @Override public void notifyAppListUnavailable(ComputerDetails details) {
                runOnUiThread(() -> {
                    if (blockingLoadSpinner != null) {
                        blockingLoadSpinner.dismiss();
                        blockingLoadSpinner = null;
                    }
                    openPublishedDesktop(details, java.util.Collections.emptyList(), null);
                });
            }

            @Override
            public void notifyComputerUpdated(final ComputerDetails details) {
                // Do nothing if updates are suspended
                if (suspendGridUpdates) {
                    return;
                }

                // Don't care about other computers
                if (!details.uuid.equalsIgnoreCase(uuidString)) {
                    return;
                }

                if (details.state == ComputerDetails.State.ONLINE && details.activeAddress != null) {
                    com.limelight.piulink.MonitorSelection.warmInventory(AppView.this,
                            details.activeAddress.address);
                }

                if (details.state == ComputerDetails.State.OFFLINE) {
                    // The PC is unreachable now
                    AppView.this.runOnUiThread(new Runnable() {
                        @Override
                        public void run() {
                            // Display a toast to the user and quit the activity
                            Toast.makeText(AppView.this, getResources().getText(R.string.lost_connection), Toast.LENGTH_SHORT).show();
                            finish();
                        }
                    });

                    return;
                }

                // Close immediately if the PC is no longer paired
                if (details.state == ComputerDetails.State.ONLINE && details.pairState != PairingManager.PairState.PAIRED) {
                    AppView.this.runOnUiThread(new Runnable() {
                        @Override
                        public void run() {
                            // Disable shortcuts referencing this PC for now
                            shortcutHelper.disableComputerShortcut(details,
                                    getResources().getString(R.string.scut_not_paired));

                            // Display a toast to the user and quit the activity
                            Toast.makeText(AppView.this, getResources().getText(R.string.scut_not_paired), Toast.LENGTH_SHORT).show();
                            finish();
                        }
                    });

                    return;
                }

                if (details.state == ComputerDetails.State.ONLINE && details.pairState == PairingManager.PairState.PAIRED) {
                    runOnUiThread(() -> handleShortcut(details));
                }

                // App list is the same or empty
                if (details.rawAppList == null || details.rawAppList.equals(lastRawApplist)) {

                    // Let's check if the running app ID changed
                    if (details.runningGameId != lastRunningAppId) {
                        // Update the currently running game using the app ID
                        lastRunningAppId = details.runningGameId;
                        updateUiWithServerinfo(details);
                    }

                    return;
                }

                lastRunningAppId = details.runningGameId;
                lastRawApplist = details.rawAppList;

                try {
                    updateUiWithAppList(NvHTTP.getAppListByReader(new StringReader(details.rawAppList)));
                    updateUiWithServerinfo(details);

                    if (blockingLoadSpinner != null) {
                        blockingLoadSpinner.dismiss();
                        blockingLoadSpinner = null;
                    }
                } catch (XmlPullParserException | IOException e) {
                    e.printStackTrace();
                }
            }
        });

        if (poller == null) {
            poller = managerBinder.createAppListPoller(computer, lastRawApplist != null && !desktopRequested(), desktopRequested());
        }
        poller.start();
    }

    void refreshRunningAppForHost(String hostUuid) {
        if (poller != null && uuidString != null && uuidString.equalsIgnoreCase(hostUuid)) {
            poller.pollNow();
        }
    }

    private void stopComputerUpdates() {
        if (poller != null) {
            poller.stop();
        }

        if (managerBinder != null) {
            managerBinder.stopPolling();
        }

        if (appGridAdapter != null) {
            appGridAdapter.cancelQueuedOperations();
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Assume we're in the foreground when created to avoid a race
        // between binding to CMS and onResume()
        inForeground = true;

        shortcutHelper = new ShortcutHelper(this);

        UiHelper.setLocale(this);

        setContentView(R.layout.activity_app_view);

        // Allow floating expanded PiP overlays while browsing apps
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            setShouldDockBigOverlays(false);
        }

        UiHelper.notifyNewRootView(this);

        shortcutHandled = savedInstanceState != null && savedInstanceState.getBoolean("shortcutHandled");
        if (savedInstanceState != null) {
            pendingTvFocusId = savedInstanceState.getInt("tvFocusId");
            libraryToolsOpen = savedInstanceState.getBoolean("libraryToolsOpen");
            librarySearchQuery = savedInstanceState.getString("librarySearchQuery", "");
            libraryFilterMode = savedInstanceState.getInt("libraryFilterMode", AppGridAdapter.FILTER_ALL);
        }
        showHiddenApps = getIntent().getBooleanExtra(SHOW_HIDDEN_APPS_EXTRA, false);
        uuidString = getIntent().getStringExtra(UUID_EXTRA);

        SharedPreferences hiddenAppsPrefs = getSharedPreferences(HIDDEN_APPS_PREF_FILENAME, MODE_PRIVATE);
        for (String hiddenAppIdStr : hiddenAppsPrefs.getStringSet(uuidString, new HashSet<String>())) {
            try { hiddenAppIds.add(Integer.parseInt(hiddenAppIdStr)); }
            catch (NumberFormatException ignored) { /* Ignore a malformed entry in an older preference. */ }
        }

        SharedPreferences favoriteAppsPrefs = getSharedPreferences(FAVORITE_APPS_PREF_FILENAME, MODE_PRIVATE);
        for (String favoriteAppIdStr : favoriteAppsPrefs.getStringSet(uuidString, new HashSet<String>())) {
            try {
                favoriteAppIds.add(Integer.parseInt(favoriteAppIdStr));
            }
            catch (NumberFormatException ignored) {
            }
        }

        recentAppIds = readRecentApps(this, uuidString);

        computerName = getIntent().getStringExtra(NAME_EXTRA);
        initializeLibraryViews();

        // Bind to the computer manager service
        serviceBound = bindService(new Intent(this, ComputerManagerService.class), serviceConnection,
                Service.BIND_AUTO_CREATE);
    }

    private void initializeLibraryViews() {
        TextView label = findViewById(R.id.appListText);
        boolean touch = UiHelper.isTouchscreenClient(this);
        TextView access = findViewById(R.id.libraryFileAccess);
        access.setVisibility(touch ? View.VISIBLE : View.GONE);
        access.setOnClickListener(v -> {
            if (fileAccessState != R.string.transfer_authorized) { checkFileAccess(); return; }
            Intent picker = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE)
                    .setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
            try { startActivityForResult(picker, PICK_FILES); }
            catch (android.content.ActivityNotFoundException error) {
                Toast.makeText(this, R.string.transfer_picker_unavailable, Toast.LENGTH_LONG).show();
            }
        });
        showFileAccess(fileAccessState);
        TextView subtitle = findViewById(R.id.appLibrarySubtitle);
        if (desktopRequested() && getIntent().getIntExtra(TV_CONTINUE_ID, 0) == 0)
            subtitle.setText(R.string.pc_opening_desktop);
        else if (touch) subtitle.setText(computerName);
        ImageButton backButton = findViewById(R.id.appBackButton);
        EditText searchInput = findViewById(R.id.appSearchInput);
        if (!touch) ((View) searchInput.getParent()).setVisibility(View.GONE);
        filterAllButton = findViewById(R.id.filterAllButton);
        filterFavoritesButton = findViewById(R.id.filterFavoritesButton);
        filterRecentButton = findViewById(R.id.filterRecentButton);
        runningAppBanner = findViewById(R.id.runningAppBanner);
        runningAppName = findViewById(R.id.runningAppName);
        appEmptyState = findViewById(R.id.appEmptyState);
        appEmptyTitle = findViewById(R.id.appEmptyTitle);
        appEmptyMessage = findViewById(R.id.appEmptyMessage);
        setTitle(computerName);
        if (touch) {
            label.setText(R.string.home_apps);
            findViewById(R.id.appBrandMark).setVisibility(View.GONE);
            View tools = findViewById(R.id.appLibraryTools);
            tools.setVisibility(View.VISIBLE);
            tools.setOnClickListener(v -> {
                libraryToolsOpen = !libraryToolsOpen;
                if (!libraryToolsOpen) {
                    if (searchInput.length() != 0) searchInput.setText("");
                    setLibraryFilter(AppGridAdapter.FILTER_ALL);
                    ((android.view.inputmethod.InputMethodManager) getSystemService(INPUT_METHOD_SERVICE))
                            .hideSoftInputFromWindow(searchInput.getWindowToken(), 0);
                }
                updateLibraryTools(searchInput, tools);
                if (libraryToolsOpen && android.animation.ValueAnimator.areAnimatorsEnabled()) {
                    View toolsRow = (View) searchInput.getParent();
                    toolsRow.setAlpha(.3f);
                    toolsRow.setTranslationY(-8 * getResources().getDisplayMetrics().density);
                    toolsRow.animate().alpha(1).translationY(0).setDuration(160).start();
                }
            });
            updateLibraryTools(searchInput, tools);
        } else label.setText(computerName);
        searchInput.setText(librarySearchQuery);
        searchInput.setSelection(searchInput.length());
        backButton.setOnClickListener(v -> leaveLibraryLevel());
        findViewById(R.id.resetLibraryFilter).setOnClickListener(v -> leaveLibraryLevel());
        searchInput.addTextChangedListener(new TextWatcher() {
            @Override
            public void beforeTextChanged(CharSequence text, int start, int count, int after) {
            }

            @Override
            public void onTextChanged(CharSequence text, int start, int before, int count) {
                librarySearchQuery = text == null ? "" : text.toString();
                if (appGridAdapter != null) {
                    appGridAdapter.setSearchQuery(librarySearchQuery);
                    refreshLibraryUi();
                }
            }

            @Override
            public void afterTextChanged(Editable editable) {
            }
        });
        filterAllButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                setLibraryFilter(AppGridAdapter.FILTER_ALL);
            }
        });
        filterFavoritesButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                setLibraryFilter(AppGridAdapter.FILTER_FAVORITES);
            }
        });
        filterRecentButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                setLibraryFilter(AppGridAdapter.FILTER_RECENT);
            }
        });
        runningAppBanner.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                if (appGridAdapter == null) {
                    return;
                }
                AppObject runningApp = appGridAdapter.findAppById(lastRunningAppId);
                if (runningApp != null) {
                    startApp(runningApp);
                }
            }
        });
        updateFilterSelection();
        refreshLibraryUi();
    }

    private void updateHiddenApps(boolean hideImmediately) {
        HashSet<String> hiddenAppIdStringSet = new HashSet<>();

        for (Integer hiddenAppId : hiddenAppIds) {
            hiddenAppIdStringSet.add(hiddenAppId.toString());
        }

        getSharedPreferences(HIDDEN_APPS_PREF_FILENAME, MODE_PRIVATE)
                .edit()
                .putStringSet(uuidString, hiddenAppIdStringSet)
                .apply();

        appGridAdapter.updateHiddenApps(hiddenAppIds, hideImmediately);
    }

    private void setLibraryFilter(int filterMode) {
        libraryFilterMode = filterMode;
        updateFilterSelection();
        if (appGridAdapter != null) {
            appGridAdapter.setFilterMode(filterMode);
            refreshLibraryUi();
        }
    }

    private void leaveLibraryLevel() {
        if (libraryToolsOpen) findViewById(R.id.appLibraryTools).performClick();
        else finish();
    }

    @Override public void onBackPressed() {
        if (libraryToolsOpen) leaveLibraryLevel();
        else super.onBackPressed();
    }

    private void updateLibraryTools(EditText searchInput, View tools) {
        int visibility = libraryToolsOpen ? View.VISIBLE : View.GONE;
        searchInput.setVisibility(visibility);
        View row = findViewById(R.id.appFilterRow);
        if (row != null) row.setVisibility(visibility);
        filterAllButton.setVisibility(visibility);
        filterFavoritesButton.setVisibility(visibility);
        filterRecentButton.setVisibility(visibility);
        tools.setSelected(libraryToolsOpen);
        tools.setContentDescription(getString(libraryToolsOpen ? R.string.close_library_search : R.string.lunar_library_tools));
        ((android.widget.ImageButton) tools).setImageResource(libraryToolsOpen ? android.R.drawable.ic_menu_close_clear_cancel : R.drawable.ic_search);
        if (Build.VERSION.SDK_INT >= 33) {
            if (libraryToolsOpen && searchBack == null) {
                searchBack = this::leaveLibraryLevel;
                getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                        android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, searchBack);
            } else if (!libraryToolsOpen && searchBack != null) {
                getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(searchBack);
                searchBack = null;
            }
        }
    }

    private void updateFilterSelection() {
        filterAllButton.setSelected(libraryFilterMode == AppGridAdapter.FILTER_ALL);
        filterFavoritesButton.setSelected(libraryFilterMode == AppGridAdapter.FILTER_FAVORITES);
        filterRecentButton.setSelected(libraryFilterMode == AppGridAdapter.FILTER_RECENT);
    }

    private void updateFavoriteApps() {
        HashSet<String> favoriteAppIdStrings = new HashSet<>();
        for (Integer favoriteAppId : favoriteAppIds) {
            favoriteAppIdStrings.add(favoriteAppId.toString());
        }
        getSharedPreferences(FAVORITE_APPS_PREF_FILENAME, MODE_PRIVATE)
                .edit()
                .putStringSet(uuidString, favoriteAppIdStrings)
                .apply();
        appGridAdapter.updateFavoriteApps(favoriteAppIds);
        refreshLibraryUi();
    }

    static ArrayList<Integer> readRecentApps(Context context, String host) {
        String value = context.getSharedPreferences(RECENT_APPS_PREF_FILENAME, MODE_PRIVATE).getString(host, "");
        ArrayList<Integer> result = new ArrayList<>();
        if (value != null) {
            for (String entry : value.split(",")) {
                try { result.add(Integer.parseInt(entry)); }
                catch (NumberFormatException ignored) { /* Old preferences may contain malformed ids. */ }
            }
        }
        return result;
    }

    private void recordRecentApp(int appId) {
        recentAppIds.remove(Integer.valueOf(appId));
        recentAppIds.add(0, appId);
        while (recentAppIds.size() > MAX_RECENT_APPS) {
            recentAppIds.remove(recentAppIds.size() - 1);
        }

        saveRecentApps();
    }

    private void saveRecentApps() {
        StringBuilder serializedRecentApps = new StringBuilder();
        for (Integer recentAppId : recentAppIds) {
            if (serializedRecentApps.length() > 0) {
                serializedRecentApps.append(',');
            }
            serializedRecentApps.append(recentAppId);
        }
        getSharedPreferences(RECENT_APPS_PREF_FILENAME, MODE_PRIVATE)
                .edit()
                .putString(uuidString, serializedRecentApps.toString())
                .apply();

        appGridAdapter.updateRecentApps(recentAppIds);
        refreshLibraryUi();
    }

    @Override
    protected void onSaveInstanceState(Bundle state) {
        super.onSaveInstanceState(state);
        state.putBoolean("shortcutHandled", shortcutHandled);
        state.putInt("tvFocusId", pendingTvFocusId);
        state.putBoolean("libraryToolsOpen", libraryToolsOpen);
        state.putString("librarySearchQuery", librarySearchQuery);
        state.putInt("libraryFilterMode", libraryFilterMode);
    }

    private void finishDesktopRequest() {
        if (desktopRequested()) {
            shortcutHandled = true;
            TextView subtitle = findViewById(R.id.appLibrarySubtitle);
            if (subtitle != null) {
                if (UiHelper.isTouchscreenClient(this)) subtitle.setText(computerName);
                else subtitle.setText(R.string.piulink_apps_subtitle);
            }
        }
    }

    private void showFileAccess(int state) {
        fileAccessState = state;
        TextView access = findViewById(R.id.libraryFileAccess);
        access.setText(state == R.string.transfer_authorized ? R.string.transfer_send_files : state);
        access.setContentDescription(state == R.string.transfer_authorized
                ? getString(R.string.transfer_send_to, computerName) : getString(state));
        access.setSelected(state == R.string.transfer_authorized);
        access.setEnabled(!checkingFileAccess);
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request != PICK_FILES || result != RESULT_OK || data == null) return;
        android.content.ClipData files = data.getClipData();
        if (files == null && data.getData() != null)
            files = android.content.ClipData.newRawUri("PiuLink", data.getData());
        if (files == null) return;
        Intent transfer = new Intent(this, TransferActivity.class).setAction(Intent.ACTION_SEND_MULTIPLE)
                .putExtra(TransferActivity.EXTRA_LIBRARY_HOST, uuidString);
        transfer.setClipData(files);
        startActivityForResult(transfer, SEND_FILES);
    }

    private void checkFileAccess() {
        if (computer == null || checkingFileAccess || !UiHelper.isTouchscreenClient(this)) return;
        final ComputerDetails target = computer;
        final Context context = getApplicationContext();
        checkingFileAccess = true;
        showFileAccess(R.string.transfer_checking);
        new Thread(() -> {
            int result = R.string.transfer_retry;
            try (TransferHostResolver resolver = new TransferHostResolver()) {
                String host = resolver.resolve(context, target);
                try (FileTransferClient client = new FileTransferClient(context, host)) {
                    client.prepareForSending(target, () -> runOnUiThread(() -> {
                        if (!isDestroyed() && !isFinishing()) showFileAccess(R.string.transfer_waiting);
                    }));
                    result = R.string.transfer_authorized;
                }
            } catch (Exception error) {
                LimeLog.warning("Library file authorization: " + error.getClass().getSimpleName());
            }
            final int state = result;
            runOnUiThread(() -> {
                checkingFileAccess = false;
                if (!isDestroyed() && !isFinishing()) showFileAccess(state);
            });
        }, "PiuLink file authorization").start();
    }

    private boolean desktopRequested() {
        return !shortcutHandled && (getIntent().getBooleanExtra(OPEN_DESKTOP, false)
                || getIntent().getIntExtra(TV_CONTINUE_ID, 0) > 0);
    }

    private void openPublishedDesktop(ComputerDetails details, List<NvApp> apps, Integer runningApp) {
        if (!desktopRequested() || !inForeground || managerBinder == null || isFinishing() || isDestroyed() ||
                !uuidString.equalsIgnoreCase(details.uuid) || details.state != ComputerDetails.State.ONLINE ||
                details.pairState != PairingManager.PairState.PAIRED || details.serverCert == null) return;
        timeDesktopEntry("published apps and running app received");
        finishDesktopRequest();
        int tvAppId = getIntent().getIntExtra(TV_CONTINUE_ID, 0);
        if (runningApp == null) {
            Toast.makeText(this, tvAppId > 0 ? R.string.lost_connection : R.string.pc_desktop_unavailable,
                    Toast.LENGTH_LONG).show();
            return;
        }
        if (tvAppId > 0) {
            NvApp target = TvLastSession.publishedApp(tvAppId, apps, hiddenAppIds);
            if (target != null) {
                openStreamTarget(target, runningApp);
                return;
            }
            if (recentAppIds.remove(Integer.valueOf(tvAppId))) saveRecentApps();
            TvLastSession.forget(this, uuidString);
            Toast.makeText(this, R.string.tv_app_unavailable, Toast.LENGTH_LONG).show();
            return;
        }
        NvApp target = findPublishedDesktop(apps);
        if (target == null) {
            Toast.makeText(this, R.string.pc_choose_desktop, Toast.LENGTH_LONG).show();
            return;
        }
        openStreamTarget(target, runningApp, getIntent().getBooleanExtra(Game.EXTRA_SUPER_SCREEN, false));
    }

    private NvApp findPublishedDesktop(List<NvApp> apps) {
        NvApp desktop = null;
        for (NvApp app : apps) {
            if (app.getAppId() <= 0 || hiddenAppIds.contains(app.getAppId()) || !app.hasDesktopName()) continue;
            if (desktop != null) return null;
            desktop = app;
        }
        return desktop;
    }

    private void handleShortcut(ComputerDetails details) {
        if (desktopRequested()) return;
        if (shortcutHandled || !inForeground || managerBinder == null || isFinishing() || isDestroyed()) return;
        shortcutHandled = true;
        int resumeId = getIntent().getIntExtra(SHORTCUT_RESUME_ID, 0);
        int targetId = getIntent().getIntExtra(Game.EXTRA_APP_ID, resumeId);
        if (targetId <= 0 || (resumeId != 0 && resumeId != details.runningGameId)) return;
        AppObject cached = appGridAdapter.findAppById(targetId);
        NvApp target = cached != null ? cached.app : new NvApp(
                getIntent().getStringExtra(Game.EXTRA_APP_NAME), targetId, getIntent().getBooleanExtra(Game.EXTRA_APP_HDR, false));
        openStreamTarget(target, details.runningGameId);
    }

    private void openStreamTarget(NvApp target, int running) {
        openStreamTarget(target, running, false);
    }

    private void openStreamTarget(NvApp target, int running, boolean superScreen) {
        if (running != 0 && running != target.getAppId()) {
            UiHelper.displayQuitConfirmationDialog(this, () -> startStreamApp(target, running, superScreen), null);
        } else {
            startStreamApp(target, 0, superScreen);
        }
    }

    private void startApp(AppObject app) {
        finishDesktopRequest();
        if (app.isSsh()) {
            com.limelight.piulink.ssh.ComputerSsh.openSsh(this, computer, false);
            return;
        }
        startStreamApp(app.app, 0, false);
    }

    private void startStreamApp(NvApp app, int confirmedRunningAppId, boolean superScreen) {
        ServerHelper.doStart(this, app, computer, managerBinder, superScreen, intent -> {
            if (confirmedRunningAppId > 0) intent.putExtra(Game.EXTRA_CONFIRMED_SWITCH_FROM, confirmedRunningAppId);
            stopComputerUpdates();
            try { startActivity(intent); }
            catch (RuntimeException error) { startComputerUpdates(); throw error; }
            if (UiHelper.isTelevision(this)) pendingTvFocusId = app.getAppId();
            recordRecentApp(app.getAppId());
        });
    }

    private void refreshLibraryUi() {
        if (appGridAdapter == null || appEmptyState == null) {
            return;
        }

        boolean showEmptyState = libraryLoaded && appGridAdapter.getCount() == 0;
        appEmptyState.setVisibility(showEmptyState ? View.VISIBLE : View.GONE);
        if (showEmptyState) {
            findViewById(R.id.resetLibraryFilter).setVisibility(libraryToolsOpen &&
                    (!librarySearchQuery.isEmpty() || libraryFilterMode != AppGridAdapter.FILTER_ALL) ? View.VISIBLE : View.GONE);
            if (!librarySearchQuery.trim().isEmpty()) {
                appEmptyTitle.setText(R.string.library_no_search_results_title);
                appEmptyMessage.setText(R.string.library_no_search_results_message);
            }
            else if (libraryFilterMode == AppGridAdapter.FILTER_FAVORITES) {
                appEmptyTitle.setText(R.string.library_no_favorites_title);
                appEmptyMessage.setText(R.string.library_no_favorites_message);
            }
            else if (libraryFilterMode == AppGridAdapter.FILTER_RECENT) {
                appEmptyTitle.setText(R.string.library_no_recent_title);
                appEmptyMessage.setText(R.string.library_no_recent_message);
            }
            else {
                appEmptyTitle.setText(R.string.library_empty_title);
                appEmptyMessage.setText(R.string.library_empty_message);
            }
        }

        AppObject runningApp = appGridAdapter.findAppById(lastRunningAppId);
        if (runningApp != null) {
            runningAppName.setText(runningApp.app.getAppName());
            runningAppBanner.setContentDescription(
                    getString(R.string.resume_app_accessibility, runningApp.app.getAppName()));
            runningAppBanner.setVisibility(View.VISIBLE);
        }
        else {
            runningAppBanner.setVisibility(View.GONE);
        }
    }

    private void loadAppsBlocking() {
        blockingLoadSpinner = SpinnerDialog.displayDialog(this, getResources().getString(R.string.applist_refresh_title),
                getResources().getString(R.string.applist_refresh_msg), true);
    }

    @Override
    protected void onDestroy() {
        if (appGridAdapter != null) appGridAdapter.close();
        if (Build.VERSION.SDK_INT >= 33 && searchBack != null) getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(searchBack);
        super.onDestroy();

        SpinnerDialog.closeDialogs(this);
        Dialog.closeDialogs(this);

        connectedBinder = null;
        managerBinder = null;
        if (serviceBound) unbindService(serviceConnection);
    }

    @Override
    protected void onResume() {
        super.onResume();

        // Display a decoder crash notification if we've returned after a crash
        UiHelper.showDecoderCrashDialog(this);

        inForeground = true;
        restoreTvFocus();
        checkFileAccess();
        startComputerUpdates();
    }

    @Override
    protected void onPause() {
        super.onPause();

        finishDesktopRequest();
        inForeground = false;
        stopComputerUpdates();
    }

    @Override
    public void onCreateContextMenu(ContextMenu menu, View v, ContextMenuInfo menuInfo) {
        super.onCreateContextMenu(menu, v, menuInfo);
        finishDesktopRequest();

        AdapterContextMenuInfo info = (AdapterContextMenuInfo) menuInfo;
        AppObject selectedApp = (AppObject) appGridAdapter.getItem(info.position);
        menu.setHeaderTitle(selectedApp.isSsh() ? getText(R.string.ssh_title) : selectedApp.app.getAppName());
        populateAppMenu(menu, selectedApp, info.targetView);
    }

    private void showAppActions(View anchor, AppObject app) {
        finishDesktopRequest();
        Menu menu = new android.widget.PopupMenu(this, anchor).getMenu();
        populateAppMenu(menu, app, anchor);
        com.limelight.utils.TouchActions.show(this, app.isSsh() ? getText(R.string.ssh_title) : app.app.getAppName(),
                menu, id -> {
                    switch (id) {
                        case START_OR_RESUME_ID: case START_WITH_QUIT: return R.drawable.ic_play;
                        case FAVORITE_APP_ID: return R.drawable.ic_star_filled;
                        case QUIT_ID: return R.drawable.ic_stream_disconnect;
                        default: return R.drawable.ic_settings;
                    }
                }, QUIT_ID, null, START_OR_RESUME_ID, START_WITH_QUIT, FAVORITE_APP_ID, QUIT_ID);
    }

    private void populateAppMenu(Menu menu, AppObject selectedApp, View anchor) {
        if (selectedApp.isSsh()) {
            menu.add(Menu.NONE, START_OR_RESUME_ID, 0, R.string.ssh_connect)
                    .setOnMenuItemClickListener(item -> { startApp(selectedApp); return true; });
            return;
        }
        if (lastRunningAppId != 0) {
            if (lastRunningAppId == selectedApp.app.getAppId()) {
                menu.add(Menu.NONE, START_OR_RESUME_ID, 1, getResources().getString(R.string.applist_menu_resume));
                menu.add(Menu.NONE, QUIT_ID, 2, getResources().getString(R.string.applist_menu_quit));
            }
            else {
                menu.add(Menu.NONE, START_WITH_QUIT, 1, getResources().getString(R.string.applist_menu_quit_and_start));
            }
        } else {
            menu.add(Menu.NONE, START_OR_RESUME_ID, 1, R.string.applist_menu_start);
        }

        // Only show the hide checkbox if this is not the currently running app or it's already hidden
        if (lastRunningAppId != selectedApp.app.getAppId() || selectedApp.isHidden) {
            MenuItem hideAppItem = menu.add(Menu.NONE, HIDE_APP_ID, 4, getResources().getString(R.string.applist_menu_hide_app));
            hideAppItem.setCheckable(true);
            hideAppItem.setChecked(selectedApp.isHidden);
        }

        menu.add(Menu.NONE, VIEW_DETAILS_ID, 5, getResources().getString(R.string.applist_menu_details));

        ImageView image = anchor.findViewById(R.id.grid_image);
        Drawable drawable = image == null ? null : image.getDrawable();
        Bitmap cover = drawable instanceof BitmapDrawable ? ((BitmapDrawable) drawable).getBitmap() : null;
        if (cover != null) {
            menu.add(Menu.NONE, CREATE_SHORTCUT_ID, 6, R.string.applist_menu_scut);
        }
        int targetId = selectedApp.app.getAppId();
        int runningId = lastRunningAppId;
        for (int i = 0; i < menu.size(); i++) {
            menu.getItem(i).setOnMenuItemClickListener(item -> handleAppAction(item, targetId, runningId, cover));
        }
    }

    private boolean handleAppAction(MenuItem item, int targetId, int runningId, Bitmap cover) {
        AppObject app = appGridAdapter.findAppById(targetId);
        if (app == null) {
            Toast.makeText(this, R.string.applist_changed, Toast.LENGTH_SHORT).show();
            return true;
        }
        switch (item.getItemId()) {
            case START_WITH_QUIT:
                // Display a confirmation dialog first
                UiHelper.displayQuitConfirmationDialog(this, new Runnable() {
                    @Override
                    public void run() {
                        startStreamApp(app.app, runningId, false);
                    }
                }, null);
                return true;

            case START_OR_RESUME_ID:
                // Resume is the same as start for us
                startApp(app);
                return true;

            case FAVORITE_APP_ID:
                if (favoriteAppIds.contains(app.app.getAppId())) {
                    favoriteAppIds.remove(app.app.getAppId());
                    Toast.makeText(this, R.string.favorite_removed, Toast.LENGTH_SHORT).show();
                }
                else {
                    favoriteAppIds.add(app.app.getAppId());
                    Toast.makeText(this, R.string.favorite_added, Toast.LENGTH_SHORT).show();
                }
                updateFavoriteApps();
                return true;

            case QUIT_ID:
                // Display a confirmation dialog first
                UiHelper.displayQuitConfirmationDialog(this, new Runnable() {
                    @Override
                    public void run() {
                        suspendGridUpdates = true;
                        ServerHelper.doQuit(AppView.this, computer,
                                app.app, managerBinder, new Runnable() {
                            @Override
                            public void run() {
                                // Trigger a poll immediately
                                suspendGridUpdates = false;
                                if (poller != null) {
                                    poller.pollNow();
                                }
                            }
                        });
                    }
                }, null);
                return true;

            case VIEW_DETAILS_ID:
                Dialog.displayResult(AppView.this, getResources().getString(R.string.title_details), app.app.toString());
                return true;

            case HIDE_APP_ID:
                if (item.isChecked()) {
                    // Transitioning hidden to shown
                    hiddenAppIds.remove(app.app.getAppId());
                }
                else {
                    // Transitioning shown to hidden
                    hiddenAppIds.add(app.app.getAppId());
                }
                updateHiddenApps(true);
                refreshLibraryUi();
                return true;

            case CREATE_SHORTCUT_ID:
                if (!shortcutHelper.createPinnedGameShortcut(computer, app.app, cover)) {
                    Toast.makeText(AppView.this, getResources().getString(R.string.unable_to_pin_shortcut), Toast.LENGTH_LONG).show();
                }
                return true;

            default:
                return false;
        }
    }

    private void updateUiWithServerinfo(final ComputerDetails details) {
        AppView.this.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                appGridAdapter.updateRunningApp(details.runningGameId);
                refreshLibraryUi();
            }
        });
    }

    private void updateUiWithAppList(final List<NvApp> appList) {
        AppView.this.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                boolean updated = false;
                if (appGridAdapter.findAppById(com.limelight.piulink.ssh.ComputerSsh.APP_ID) == null) {
                    appGridAdapter.addApp(new AppObject(new NvApp(getString(R.string.ssh_title), com.limelight.piulink.ssh.ComputerSsh.APP_ID, false)));
                    updated = true;
                }

                // First handle app updates and additions
                for (NvApp app : appList) {
                    boolean foundExistingApp = false;

                    // Try to update an existing app in the complete list first.
                    AppObject existingApp = appGridAdapter.findAppById(app.getAppId());
                    if (existingApp != null) {
                        if (!existingApp.app.getAppName().equals(app.getAppName())) {
                            existingApp.app.setAppName(app.getAppName());
                            updated = true;
                        }
                        foundExistingApp = true;
                    }

                    if (!foundExistingApp) {
                        // This app must be new
                        appGridAdapter.addApp(new AppObject(app));

                        // We could have a leftover shortcut from last time this PC was paired
                        // or if this app was removed then added again. Enable those shortcuts
                        // again if present.
                        shortcutHelper.enableAppShortcut(computer, app);

                        updated = true;
                    }
                }

                // Next handle app removals
                for (AppObject existingApp : appGridAdapter.getAllAppsSnapshot()) {
                    if (existingApp.isSsh()) continue;
                    boolean foundExistingApp = false;

                    // Check if this app is in the latest list
                    for (NvApp app : appList) {
                        if (existingApp.app.getAppId() == app.getAppId()) {
                            foundExistingApp = true;
                            break;
                        }
                    }

                    // This app was removed in the latest app list
                    if (!foundExistingApp) {
                        shortcutHelper.disableAppShortcut(computer, existingApp.app, "App removed from PC");
                        appGridAdapter.removeApp(existingApp);
                        updated = true;
                    }
                }

                if (updated) {
                    appGridAdapter.refreshLibrary();
                }
                libraryLoaded = true;
                refreshLibraryUi();
                restoreTvFocus();
            }
        });
    }

    @Override
    public int getAdapterFragmentLayoutId() {
        return PreferenceConfiguration.readPreferences(AppView.this).smallIconMode ?
                    R.layout.app_grid_view_small : R.layout.app_grid_view;
    }

    @Override
    public void receiveAbsListView(AbsListView listView) {
        listView.setAdapter(appGridAdapter);
        listView.setOnItemClickListener(new OnItemClickListener() {
            @Override
            public void onItemClick(AdapterView<?> arg0, View arg1, int pos,
                                    long id) {
                AppObject app = (AppObject) appGridAdapter.getItem(pos);

                if (UiHelper.isTelevision(AppView.this) && !app.isSsh()) {
                    finishDesktopRequest();
                    openStreamTarget(app.app, lastRunningAppId);
                    return;
                }
                // A tap resumes the same app on touch devices. Long press still exposes its actions.
                if (!app.isSsh() && lastRunningAppId != 0 &&
                        (lastRunningAppId != app.app.getAppId() || !UiHelper.isTouchscreenClient(AppView.this))) {
                    if (UiHelper.isTouchscreenClient(AppView.this)) showAppActions(arg1, app);
                    else openContextMenu(arg1);
                } else {
                    startApp(app);
                }
            }
        });
        UiHelper.applyStatusBarPadding(listView);
        if (UiHelper.isTouchscreenClient(this)) {
            listView.setOnItemLongClickListener((parent, view, pos, id) -> {
                showAppActions(view, (AppObject) appGridAdapter.getItem(pos));
                return true;
            });
        } else registerForContextMenu(listView);
        listView.requestFocus();
        refreshLibraryUi();
        restoreTvFocus();
    }

    private void restoreTvFocus() {
        if (pendingTvFocusId <= 0 || !inForeground || appGridAdapter == null) return;
        AbsListView grid = findViewById(R.id.fragmentView);
        if (grid == null || grid.getAdapter() != appGridAdapter) return;
        for (int i = 0; i < appGridAdapter.getCount(); i++) {
            AppObject app = (AppObject) appGridAdapter.getItem(i);
            if (app.app.getAppId() == pendingTvFocusId) {
                grid.requestFocus();
                grid.setSelection(i);
                pendingTvFocusId = 0;
                break;
            }
        }
    }

    public static class AppObject {
        public boolean isSsh() { return app.getAppId() == com.limelight.piulink.ssh.ComputerSsh.APP_ID; }
        public final NvApp app;
        public boolean isRunning;
        public boolean isHidden;
        public boolean isFavorite;
        public int recentRank = -1;

        public AppObject(NvApp app) {
            if (app == null) {
                throw new IllegalArgumentException("app must not be null");
            }
            this.app = app;
        }

        @Override
        public String toString() {
            return app.getAppName();
        }
    }
    @Override protected void onStop() {
        com.limelight.piulink.MonitorSelection.cancel(this);
        super.onStop();
    }
}
