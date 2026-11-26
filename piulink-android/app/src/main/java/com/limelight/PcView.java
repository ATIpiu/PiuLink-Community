package com.limelight;

import java.io.FileNotFoundException;
import java.io.IOException;
import java.net.UnknownHostException;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

import com.limelight.binding.PlatformBinding;
import com.limelight.binding.crypto.AndroidCryptoProvider;
import com.limelight.computers.ComputerManagerListener;
import com.limelight.computers.ComputerManagerService;
import com.limelight.grid.PcGridAdapter;
import com.limelight.grid.assets.DiskAssetLoader;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.nvstream.http.PairingManager.PairState;
import com.limelight.nvstream.wol.WakeOnLanSender;
import com.limelight.piulink.ConnectionAssistantDialog;
import com.limelight.preferences.AddComputerManually;
import com.limelight.preferences.GlPreferences;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.preferences.StreamSettings;
import com.limelight.ui.AdapterFragment;
import com.limelight.ui.AdapterFragmentCallbacks;
import com.limelight.utils.Dialog;
import com.limelight.utils.HelpLauncher;
import com.limelight.utils.NetHelper;
import com.limelight.utils.TailscaleLauncher;
import com.limelight.utils.ServerHelper;
import com.limelight.piulink.transfer.FileTransferClient;
import com.limelight.utils.ShortcutHelper;
import com.limelight.utils.UiHelper;

import android.app.Activity;
import android.app.ActivityManager;
import android.app.Service;
import android.content.ComponentName;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.res.Configuration;
import android.opengl.GLSurfaceView;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.preference.PreferenceManager;
import android.view.ContextMenu;
import android.view.Menu;
import android.view.MenuItem;
import android.view.View;
import android.view.ViewGroup;
import android.view.ContextMenu.ContextMenuInfo;
import android.view.View.OnClickListener;
import android.widget.AbsListView;
import android.widget.AdapterView;
import android.widget.TextView;
import android.widget.AdapterView.OnItemClickListener;
import android.widget.Toast;
import android.widget.AdapterView.AdapterContextMenuInfo;

import org.xmlpull.v1.XmlPullParserException;

import javax.microedition.khronos.egl.EGLConfig;
import javax.microedition.khronos.opengles.GL10;

public class PcView extends Activity implements AdapterFragmentCallbacks {
    private com.limelight.piulink.HomeTabs homeTabs;
    public static final String ACTION_SHOW_COMPUTERS = "labs.atipiu.piulink.SHOW_COMPUTERS";
    private int savedHomeTab;
    private int savedSettingsSection = -1;

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        if (ACTION_SHOW_COMPUTERS.equals(intent.getAction())) {
            savedHomeTab = 0;
            if (homeTabs != null) homeTabs.select(0);
        }
    }

    public void reloadHomeSettings() {
        if (homeTabs != null) homeTabs.settings.reload();
    }

    public void refreshHomePreferences() {
        if (pcGridAdapter != null) {
            pcGridAdapter.updateLayoutWithPreferences(this, PreferenceConfiguration.readPreferences(this));
            pcGridAdapter.notifyDataSetChanged();
        }
    }

    @Override public void onBackPressed() {
        if (homeTabs != null && homeTabs.back()) return;
        super.onBackPressed();
    }

    @Override protected void onSaveInstanceState(Bundle state) {
        if (homeTabs != null) {
            state.putBoolean("home_settings_open", homeTabs.selected() == 1);
            state.putInt("home_section", homeTabs.settings.getSection());
        }
        super.onSaveInstanceState(state);
    }
    private View noPcFoundLayout;
    private PcGridAdapter pcGridAdapter;
    private TextView tvContinue;
    private boolean homeInteracted;

    @Override public void onUserInteraction() {
        super.onUserInteraction();
        homeInteracted = true;
    }
    private ShortcutHelper shortcutHelper;
    private ComputerManagerService.ComputerManagerBinder managerBinder;
    private boolean serviceBound;
    private android.app.AlertDialog connectionAssistantDialog;
    private android.app.AlertDialog pairingPinDialog;
    private NvHTTP pinConnection;
    private boolean pairingInProgress;
    private boolean openingLibrary;
    private static final int REQUEST_DESKTOP_ROUTE = 4803;
    private Thread desktopRouteRequest;
    private android.app.ProgressDialog routeProgress;
    private String manualDesktopId;
    private volatile boolean pairingCancelled;
    private boolean freezeUpdates, runningPolling, inForeground, completeOnCreateCalled;
    private final java.util.Map<String, String> warmedDisplayRoutes = new java.util.HashMap<>();
    private final ServiceConnection serviceConnection = new ServiceConnection() {
        public void onServiceConnected(ComponentName className, IBinder binder) {
            final ComputerManagerService.ComputerManagerBinder localBinder =
                    ((ComputerManagerService.ComputerManagerBinder)binder);

            // ServiceConnection callbacks run on the main thread. Publish the binder here instead
            // of from the worker below so onPause()/onResume() cannot observe a stale null value.
            managerBinder = localBinder;
            startComputerUpdates();
            openSelectedDesktop();

            // Wait in a separate thread to avoid stalling the UI
            new Thread() {
                @Override
                public void run() {
                    // Wait for the binder to be ready
                    localBinder.waitForReady();

                    // Activity lifecycle state belongs to the main thread. Posting this also
                    // makes a host added in the secondary activity appear immediately when the
                    // computer list resumes.
                    PcView.this.runOnUiThread(new Runnable() {
                        @Override
                        public void run() {
                            if (managerBinder == localBinder) {
                                startComputerUpdates();
                            }
                        }
                    });

                    // Force a keypair to be generated early to avoid discovery delays
                    new AndroidCryptoProvider(PcView.this).getClientCertificate();
                }
            }.start();
        }

        public void onServiceDisconnected(ComponentName className) {
            managerBinder = null;
        }
    };

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);

        // Only reinitialize views if completeOnCreate() was called
        // before this callback. If it was not, completeOnCreate() will
        // handle initializing views with the config change accounted for.
        // This is not prone to races because both callbacks are invoked
        // in the main thread.
        if (completeOnCreateCalled) {
            // Reinitialize views just in case orientation changed
            initializeViews();
        }
    }

    private final static int PAIR_ID = 2;
    private final static int UNPAIR_ID = 3;
    private final static int WOL_ID = 4;
    private final static int DELETE_ID = 5;
    private final static int RESUME_ID = 6;
    private final static int QUIT_ID = 7;
    private final static int VIEW_DETAILS_ID = 8;
    private final static int FULL_APP_LIST_ID = 9;
    private final static int TEST_NETWORK_ID = 10;
    private final static int GAMESTREAM_EOL_ID = 11;
    private final static int TERMINAL_ID = 12;
    private static final int CONNECTION_METHODS_ID = 25;
    private static final int AUTO_MIC_ID = 13;

    private void initializeViews() {
        if (homeTabs != null) {
            savedHomeTab = homeTabs.selected();
            savedSettingsSection = homeTabs.settings.getSection();
        }
        android.app.Fragment previousSettings = getFragmentManager().findFragmentByTag("home_settings");
        if (previousSettings != null) {
            getFragmentManager().beginTransaction().remove(previousSettings).commitAllowingStateLoss();
            getFragmentManager().executePendingTransactions();
        }
        boolean touchHome = UiHelper.isTouchscreenClient(this);
        setContentView(UiHelper.isTelevision(this) ? R.layout.activity_pc_tv :
                touchHome ? R.layout.activity_pc_touch : R.layout.activity_pc_view);
        tvContinue = findViewById(R.id.tvContinue);
        if (touchHome) {
            View body = findViewById(R.id.computersPage);
            int maxWidth = Math.round(880 * getResources().getDisplayMetrics().density);
            ((View) body.getParent()).addOnLayoutChangeListener((page, left, top, right, bottom, ol, ot, or, ob) -> {
                int width = Math.min(right - left, maxWidth);
                ViewGroup.LayoutParams params = body.getLayoutParams();
                if (width > 0 && width != params.width) {
                    params.width = width;
                    body.setLayoutParams(params);
                }
            });
        }

        UiHelper.notifyNewRootView(this);

        // Allow floating expanded PiP overlays while browsing PCs
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            setShouldDockBigOverlays(false);
        }

        // Set default preferences if we've never been run
        PreferenceManager.setDefaultValues(this, R.xml.preferences, false);

        // Set the correct layout for the PC grid
        pcGridAdapter.updateLayoutWithPreferences(this, PreferenceConfiguration.readPreferences(this));

        if (!touchHome) {
            View settingsButton = findViewById(R.id.settingsButton);
            View terminalButton = findViewById(R.id.terminalButton);
            View tailscaleButton = findViewById(R.id.tailscaleButton);
            if (getResources().getConfiguration().orientation == Configuration.ORIENTATION_PORTRAIT) {
                ((View) settingsButton.getParent()).setVisibility(View.GONE);
            } else {
                settingsButton.setVisibility(View.GONE);
                terminalButton.setVisibility(View.GONE);
                tailscaleButton.setVisibility(View.GONE);
            }
            settingsButton.setOnClickListener(v -> startActivity(new Intent(this, StreamSettings.class)));
            terminalButton.setOnClickListener(v -> startActivity(new Intent(this, com.limelight.piulink.ssh.TerminalHub.class)));
            tailscaleButton.setOnClickListener(v -> TailscaleLauncher.openAppOrPlayStore(this));
        }
        homeTabs = new com.limelight.piulink.HomeTabs(this);
        homeTabs.settings.select(savedSettingsSection);
        homeTabs.select(savedHomeTab);
        findViewById(R.id.manuallyAddPc).setOnClickListener(v -> openAddComputer());
        View history = findViewById(R.id.homeHistory);
        if (history != null) history.setOnClickListener(v -> openRoutePicker(null));
        View emptyAddComputerButton = findViewById(R.id.emptyAddPcButton);
        View emptyTailscaleButton = findViewById(R.id.emptyTailscaleButton);
        View emptyHelpButton = findViewById(R.id.emptyHelpButton);

        emptyAddComputerButton.setOnClickListener(new OnClickListener() {
            @Override
            public void onClick(View v) {
                openAddComputer();
            }
        });
        emptyTailscaleButton.setOnClickListener(new OnClickListener() {
            @Override
            public void onClick(View v) {
                TailscaleLauncher.openAppOrPlayStore(PcView.this);
            }
        });
        emptyHelpButton.setOnClickListener(new OnClickListener() {
            @Override
            public void onClick(View v) {
                startActivity(new Intent(PcView.this, com.limelight.piulink.SetupActivity.class));
            }
        });

        getFragmentManager().beginTransaction()
            .replace(R.id.pcFragmentContainer, new AdapterFragment())
            .commitAllowingStateLoss();

        noPcFoundLayout = findViewById(R.id.no_pc_found_layout);
        if (pcGridAdapter.getCount() == 0) {
            noPcFoundLayout.setVisibility(View.VISIBLE);
        }
        else {
            noPcFoundLayout.setVisibility(View.INVISIBLE);
        }
        pcGridAdapter.notifyDataSetChanged();
        refreshTvContinue();
        updateSetupStatus();
    }

    private void autoOpenDesktop(ComputerDetails pc) {
        openSavedDesktop(pc, false);
    }

    private void openSavedDesktop(ComputerDetails pc, boolean invitationOnly) {
        if (desktopRouteRequest != null || openingLibrary) return;
        if (pc.serverCert == null) {
            openHost(pc);
            return;
        }
        ComputerManagerService.ComputerManagerBinder manager = managerBinder;
        if (manager == null) {
            Toast.makeText(this, R.string.error_manager_not_running, Toast.LENGTH_SHORT).show();
            return;
        }
        desktopRouteRequest = new Thread(() -> {
            Thread request = Thread.currentThread();
            ComputerDetails peer = null;
            try {
                peer = invitationOnly ? manager.resolveSavedInvitation(pc.uuid) : manager.resolveDesktopRoute(pc.uuid);
            } catch (InterruptedException cancelled) {
                return;
            } catch (IOException | RuntimeException error) {
                LimeLog.warning("Desktop route unavailable (" + error.getClass().getSimpleName() + ")");
            }
            ComputerDetails result = peer;
            runOnUiThread(() -> {
                if (desktopRouteRequest != request || !inForeground || isFinishing()) return;
                cancelRouteRequest();
                if (manager == managerBinder && result != null && manager.selectDesktopRoute(result)) {
                    doAppList(result, false, false, true);
                } else {
                    DiagnosticReport.connection(this, "host", "unreachable", 0);
                    openRoutePicker(pc, !invitationOnly);
                }
            });
        }, "PiuLink desktop route");
        Thread request = desktopRouteRequest;
        request.start();
        getWindow().getDecorView().postDelayed(() -> {
            if (desktopRouteRequest != request || !inForeground || isFinishing()) return;
            routeProgress = new android.app.ProgressDialog(this);
            routeProgress.setMessage(getString(R.string.route_connecting, pc.name));
            routeProgress.setOnCancelListener(dialog -> cancelRouteRequest());
            routeProgress.show();
        }, 300);
    }

    private void cancelRouteRequest() {
        if (desktopRouteRequest != null) {
            desktopRouteRequest.interrupt();
            desktopRouteRequest = null;
        }
        if (routeProgress != null) {
            routeProgress.dismiss();
            routeProgress = null;
        }
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == REQUEST_DESKTOP_ROUTE && result == RESULT_OK && data != null) {
            manualDesktopId = data.getStringExtra(AddComputerManually.EXTRA_ROUTE_HOST);
            openSelectedDesktop();
        }
    }

    private void openSelectedDesktop() {
        if (manualDesktopId == null || !inForeground || managerBinder == null) return;
        ComputerDetails peer = managerBinder.getComputer(manualDesktopId);
        manualDesktopId = null;
        if (peer != null && peer.serverCert != null) doAppList(peer, false, false, true);
        else if (peer != null) openHost(peer);
    }

    private void openRoutePicker(ComputerDetails pc) {
        openRoutePicker(pc, false);
    }

    private void openRoutePicker(ComputerDetails pc, boolean autoFailed) {
        if (managerBinder == null) {
            Toast.makeText(this, R.string.error_manager_not_running, Toast.LENGTH_SHORT).show();
            return;
        }
        int title = pc == null ? R.string.connection_history :
                autoFailed ? R.string.route_choose_after_failure : R.string.connection_methods;
        com.limelight.preferences.ConnectionRoutePicker.show(this, managerBinder, pc == null ? null : pc.uuid, title, (host, route) ->
                startActivityForResult(new Intent(this, AddComputerManually.class)
                        .putExtra(AddComputerManually.EXTRA_ROUTE_HOST, host.uuid)
                        .putExtra(AddComputerManually.EXTRA_ROUTE_KEY, route.key), REQUEST_DESKTOP_ROUTE));
    }

    private void openAddComputer() {
        startActivityForResult(new Intent(this, AddComputerManually.class), REQUEST_DESKTOP_ROUTE);
    }

    private void updateSetupStatus() {
        TextView networkStatus = findViewById(R.id.setupPrivateNetworkStatus);
        View networkDot = findViewById(R.id.setupPrivateNetworkDot);
        if (networkStatus == null || networkDot == null) {
            return;
        }

        if (NetHelper.isActiveNetworkVpn(this)) {
            setSetupStatus(networkStatus, networkDot,
                    R.string.setup_private_network_active,
                    R.color.piulink_success, R.drawable.bg_status_ready);
        }
        else if (TailscaleLauncher.isInstalled(this)) {
            setSetupStatus(networkStatus, networkDot,
                    R.string.setup_private_network_available,
                    R.color.piulink_warning, R.drawable.bg_status_attention);
        }
        else {
            setSetupStatus(networkStatus, networkDot,
                    R.string.setup_private_network_missing,
                    R.color.piulink_text_secondary, R.drawable.bg_status_neutral);
        }
    }

    private void setSetupStatus(TextView label, View dot, int textResource,
                                int colorResource, int dotResource) {
        label.setText(textResource);
        label.setTextColor(android.os.Build.VERSION.SDK_INT >= 23
                ? getResources().getColor(colorResource, getTheme()) : getResources().getColor(colorResource));
        dot.setBackgroundResource(dotResource);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        if (savedInstanceState != null) {
            savedHomeTab = ACTION_SHOW_COMPUTERS.equals(getIntent().getAction()) ? 0 : (savedInstanceState.getBoolean("home_settings_open", savedInstanceState.getInt("home_tab", 0) == 2) ? 1 : 0);
            savedSettingsSection = savedInstanceState.getInt("home_section", -1);
        }
        if (ACTION_SHOW_COMPUTERS.equals(getIntent().getAction())) getIntent().setAction(null);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                    android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::onBackPressed);
        }

        // Assume we're in the foreground when created to avoid a race
        // between binding to CMS and onResume()
        inForeground = true;

        // Create a GLSurfaceView to fetch GLRenderer unless we have
        // a cached result already.
        final GlPreferences glPrefs = GlPreferences.readPreferences(this);
        if (!glPrefs.savedFingerprint.equals(Build.FINGERPRINT) || glPrefs.glRenderer.isEmpty()) {
            GLSurfaceView surfaceView = new GLSurfaceView(this);
            surfaceView.setRenderer(new GLSurfaceView.Renderer() {
                @Override
                public void onSurfaceCreated(GL10 gl10, EGLConfig eglConfig) {
                    // Save the GLRenderer string so we don't need to do this next time
                    glPrefs.glRenderer = gl10.glGetString(GL10.GL_RENDERER);
                    glPrefs.savedFingerprint = Build.FINGERPRINT;
                    glPrefs.writePreferences();

                    LimeLog.info("Fetched GL Renderer: " + glPrefs.glRenderer);

                    runOnUiThread(new Runnable() {
                        @Override
                        public void run() {
                            completeOnCreate();
                        }
                    });
                }

                @Override
                public void onSurfaceChanged(GL10 gl10, int i, int i1) {
                }

                @Override
                public void onDrawFrame(GL10 gl10) {
                }
            });
            setContentView(surfaceView);
        }
        else {
            LimeLog.info("Cached GL Renderer: " + glPrefs.glRenderer);
            completeOnCreate();
        }
    }

    private void completeOnCreate() {
        completeOnCreateCalled = true;

        shortcutHelper = new ShortcutHelper(this);

        UiHelper.setLocale(this);

        // Bind to the computer manager service
        serviceBound = bindService(new Intent(PcView.this, ComputerManagerService.class), serviceConnection,
                Service.BIND_AUTO_CREATE);

        pcGridAdapter = new PcGridAdapter(this, PreferenceConfiguration.readPreferences(this),
                UiHelper.isTouchscreenClient(this) ? this::autoOpenDesktop : null, this::openHost,
                computer -> doAppList(computer, false, false, true, 0, true),
                computer -> openSavedDesktop(computer, true));

        initializeViews();
        android.content.SharedPreferences setup = getSharedPreferences("connection_setup", MODE_PRIVATE);
        if (!setup.getBoolean("introduced", false)) {
            new Thread(() -> {
                com.limelight.computers.ComputerDatabaseManager database =
                        new com.limelight.computers.ComputerDatabaseManager(getApplicationContext());
                boolean empty;
                try { empty = database.getAllComputers().isEmpty(); }
                finally { database.close(); }
                runOnUiThread(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    setup.edit().putBoolean("introduced", true).apply();
                    if (empty) startActivity(new Intent(PcView.this,
                            com.limelight.piulink.SetupActivity.class));
                });
            }, "PiuLink first connection guide").start();
        }
    }

    private void startComputerUpdates() {
        // startPolling() is idempotent: it reports every saved tuple and starts only missing
        // worker threads. Always reattach while foregrounded so a lifecycle race cannot leave an
        // empty grid until the whole app is restarted.
        if (managerBinder != null && inForeground && !pairingInProgress) {
            freezeUpdates = false;
            managerBinder.startPolling(new ComputerManagerListener() {
                @Override
                public void notifyComputerUpdated(final ComputerDetails details) {
                    if (!freezeUpdates) {
                        PcView.this.runOnUiThread(new Runnable() {
                            @Override
                            public void run() {
                                if (!freezeUpdates && inForeground) updateComputer(details);
                            }
                        });

                        // Add a launcher shortcut for this PC (off the main thread to prevent ANRs)
                        if (details.pairState == PairState.PAIRED) {
                            shortcutHelper.createAppViewShortcutForOnlineHost(details);
                        }
                    }
                }
            });
            runningPolling = true;
        }
    }

    private void stopComputerUpdates(boolean wait) {
        freezeUpdates = true;
        if (managerBinder != null) {
            if (!runningPolling) {
                runningPolling = false;
                return;
            }

            managerBinder.stopPolling();

            if (wait) {
                managerBinder.waitForPollingStopped();
            }
        }
        runningPolling = false;
    }

    @Override
    public void onDestroy() {
        cancelPinPairing();
        dismissPairingPinDialog();
        super.onDestroy();

        managerBinder = null;
        if (serviceBound) unbindService(serviceConnection);
    }

    @Override
    protected void onResume() {
        super.onResume();
        openingLibrary = false;
        if (homeTabs != null) homeTabs.refresh();

        // Display a decoder crash notification if we've returned after a crash
        UiHelper.showDecoderCrashDialog(this);

        inForeground = true;
        openSelectedDesktop();
        startComputerUpdates();
        if (completeOnCreateCalled) {
            refreshTvContinue();
            updateSetupStatus();
        }
    }

    @Override
    protected void onPause() {
        super.onPause();

        inForeground = false;
        cancelRouteRequest();
        warmedDisplayRoutes.clear();
        stopComputerUpdates(false);
    }

    @Override
    protected void onStop() {
        com.limelight.piulink.MonitorSelection.cancel(this);
        super.onStop();

        // Pairing can legitimately outlive a transient onStop() (permission UI, notification
        // shade, task switch, etc). The PIN prompt has its own lifecycle and must not be swept
        // up with ordinary informational dialogs while Sunshine is waiting for the PIN.
        Dialog.closeDialogs(this);
        if (connectionAssistantDialog != null) {
            connectionAssistantDialog.dismiss();
            connectionAssistantDialog = null;
        }
    }

    @Override
    public void onCreateContextMenu(ContextMenu menu, View v, ContextMenuInfo menuInfo) {
        stopComputerUpdates(false);

        // Call superclass
        super.onCreateContextMenu(menu, v, menuInfo);
                
        AdapterContextMenuInfo info = (AdapterContextMenuInfo) menuInfo;
        ComputerObject computer = (ComputerObject) pcGridAdapter.getItem(info.position);

        // Add a header with PC status details
        menu.clearHeader();
        String headerTitle = computer.details.name + " - ";
        switch (computer.details.state)
        {
            case ONLINE:
                headerTitle += getResources().getString(R.string.pcview_menu_header_online);
                break;
            case OFFLINE:
                menu.setHeaderIcon(R.drawable.ic_pc_offline);
                headerTitle += getResources().getString(R.string.pcview_menu_header_offline);
                break;
            case UNKNOWN:
                headerTitle += getResources().getString(R.string.pcview_menu_header_unknown);
                break;
        }

        menu.setHeaderTitle(headerTitle);
        if (computer.details.uuid != null) menu.add(Menu.NONE, CONNECTION_METHODS_ID, 0, R.string.connection_methods);
        menu.add(Menu.NONE, TERMINAL_ID, 0, R.string.ssh_title);

        if (computer.details.pairState == PairState.PAIRED && computer.details.uuid != null) {
            menu.add(Menu.NONE, AUTO_MIC_ID, 5, R.string.auto_mic_host_setting).setCheckable(true)
                    .setChecked(Game.autoMicEnabled(this, computer.details.uuid));
        }

        // Inflate the context menu
        if (computer.details.state == ComputerDetails.State.OFFLINE ||
            computer.details.state == ComputerDetails.State.UNKNOWN) {
            menu.add(Menu.NONE, WOL_ID, 1, getResources().getString(R.string.pcview_menu_send_wol));
            menu.add(Menu.NONE, GAMESTREAM_EOL_ID, 2, getResources().getString(R.string.pcview_menu_eol));
        }
        else if (computer.details.pairState != PairState.PAIRED) {
            menu.add(Menu.NONE, PAIR_ID, 1, getResources().getString(R.string.pcview_menu_pair_pc));
            if (computer.details.nvidiaServer) {
                menu.add(Menu.NONE, GAMESTREAM_EOL_ID, 2, getResources().getString(R.string.pcview_menu_eol));
            }
        }
        else {
            // Keep an explicit repair path available when the Host has been reinstalled
            // or regenerated its certificate while Android still has the old pin.
            menu.add(Menu.NONE, PAIR_ID, 0, getResources().getString(R.string.pcview_menu_pair_pc));
            if (computer.details.runningGameId != 0) {
                menu.add(Menu.NONE, RESUME_ID, 1, getResources().getString(R.string.applist_menu_resume));
                menu.add(Menu.NONE, QUIT_ID, 2, getResources().getString(R.string.applist_menu_quit));
            }

            if (computer.details.nvidiaServer) {
                menu.add(Menu.NONE, GAMESTREAM_EOL_ID, 3, getResources().getString(R.string.pcview_menu_eol));
            }

            menu.add(Menu.NONE, FULL_APP_LIST_ID, 4, getResources().getString(R.string.pcview_menu_app_list));
        }

        menu.add(Menu.NONE, TEST_NETWORK_ID, 5, getResources().getString(R.string.pcview_menu_connection_assistant));
        menu.add(Menu.NONE, DELETE_ID, 6, getResources().getString(R.string.pcview_menu_delete_pc));
        menu.add(Menu.NONE, VIEW_DETAILS_ID, 7, getResources().getString(R.string.pcview_menu_details));
        final int runningId = computer.details.runningGameId;
        for (int i = 0; i < menu.size(); i++) {
            menu.getItem(i).setOnMenuItemClickListener(item -> handleComputerAction(item, computer, runningId));
        }
    }

    @Override
    public void onContextMenuClosed(Menu menu) {
        // For some reason, this gets called again _after_ onPause() is called on this activity.
        // startComputerUpdates() manages this and won't actual start polling until the activity
        // returns to the foreground.
        startComputerUpdates();
    }

    private void doPair(final ComputerDetails computer) {
        doPair(computer, true);
    }

    private void doPair(final ComputerDetails computer, boolean announcePairing) {
        if (computer.state == ComputerDetails.State.OFFLINE || computer.activeAddress == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.pair_pc_offline), Toast.LENGTH_SHORT).show();
            return;
        }
        if (managerBinder == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
            return;
        }

        if (pairingInProgress) return;
        final ComputerManagerService.ComputerManagerBinder pairingBinder = managerBinder;
        pairingInProgress = true;
        pairingCancelled = false;
        stopComputerUpdates(false);
        if (announcePairing) {
            Toast.makeText(PcView.this, getResources().getString(R.string.pairing), Toast.LENGTH_SHORT).show();
        }
        new Thread(new Runnable() {
            @Override
            public void run() {
                NvHTTP httpConn = null;
                PairingManager pairingManager = null;
                String message;
                boolean success = false;
                try {
                    // Stop updates and wait while pairing
                    pairingBinder.waitForPollingStopped();
                    if (pairingCancelled) throw new java.io.InterruptedIOException("Pairing cancelled");

                    httpConn = new NvHTTP(ServerHelper.getCurrentAddressFromComputer(computer),
                            computer.httpsPort, pairingBinder.getUniqueId(),
                            computer.pairState == PairState.PAIRED ? computer.serverCert : null,
                            PlatformBinding.getCryptoProvider(PcView.this));
                    boolean boundToLocalNetwork = NetHelper.bindToDirectLocalNetwork(PcView.this, httpConn,
                            ServerHelper.getCurrentAddressFromComputer(computer).address);
                    boolean alreadyPaired;
                    try {
                        alreadyPaired = httpConn.getPairState() == PairState.PAIRED;
                    } catch (IOException | XmlPullParserException staleCertificateFailure) {
                        if (boundToLocalNetwork &&
                                NetHelper.isNetworkBindingDenied(staleCertificateFailure)) {
                            // Some Android vendor/VPN combinations expose the Wi-Fi underlay but
                            // reject Network-bound sockets with EPERM. Polling already retries this
                            // way; pairing must do the same or it fails before reading the Host's
                            // trusted auto-pair ticket.
                            LimeLog.warning("Direct network binding was denied while pairing " +
                                    ServerHelper.getCurrentAddressFromComputer(computer) +
                                    "; retrying through the Android default route");
                            httpConn = new NvHTTP(ServerHelper.getCurrentAddressFromComputer(computer),
                                    computer.httpsPort, pairingBinder.getUniqueId(), null,
                                    PlatformBinding.getCryptoProvider(PcView.this));
                            // The bound request may have failed before it could determine whether
                            // the saved certificate is still current. Start from public HTTP so a
                            // reinstalled Host can issue a fresh trusted auto-pair ticket without
                            // waiting on the stale HTTPS certificate first.
                            alreadyPaired = false;
                        }
                        else if (computer.serverCert == null) {
                            throw staleCertificateFailure;
                        }
                        else {
                            // A Host reinstall or engine certificate rotation invalidates the old
                            // pin. Retry discovery without it so this action repairs pairing instead
                            // of failing before the new handshake starts.
                            httpConn = new NvHTTP(ServerHelper.getCurrentAddressFromComputer(computer),
                                    computer.httpsPort, pairingBinder.getUniqueId(), null,
                                    PlatformBinding.getCryptoProvider(PcView.this));
                            NetHelper.bindToDirectLocalNetwork(PcView.this, httpConn,
                                    ServerHelper.getCurrentAddressFromComputer(computer).address);
                            alreadyPaired = false;
                        }
                    }
                    if (alreadyPaired) {
                        // Don't display any toast, but open the app list
                        message = null;
                        success = true;
                    }
                    else {
                        final String serverInfo = httpConn.getServerInfo(true);
                        final boolean trustedAutoPair = PairingManager.isTrustedAutoPairEnabled(serverInfo);
                        final String pinStr = trustedAutoPair ? PairingManager.TRUSTED_AUTO_PAIR_PIN
                                : PairingManager.generatePinString();

                        if (trustedAutoPair) {
                            runOnUiThread(() -> Toast.makeText(PcView.this,
                                    R.string.pairing_trusted, Toast.LENGTH_SHORT).show());
                        } else {
                            showPairingPinDialogAndWait(pinStr, httpConn);
                        }

                        if (pairingCancelled) throw new java.io.InterruptedIOException("Pairing cancelled");
                        pairingManager = httpConn.getPairingManager();

                        PairState pairState = pairingManager.pair(serverInfo, pinStr);
                        if (pairState == PairState.PIN_WRONG) {
                            message = getResources().getString(R.string.pair_incorrect_pin);
                        }
                        else if (pairState == PairState.FAILED) {
                            if (computer.runningGameId != 0) {
                                message = getResources().getString(R.string.pair_pc_ingame);
                            }
                            else {
                                message = getResources().getString(R.string.pair_fail);
                            }
                        }
                        else if (pairState == PairState.ALREADY_IN_PROGRESS) {
                            message = getResources().getString(R.string.pair_already_in_progress);
                        }
                        else if (pairState == PairState.PAIRED) {
                            // Just navigate to the app view without displaying a toast
                            message = null;
                            success = true;

                            // Pin this certificate for later HTTPS use
                            persistPairingCertificate(computer, pairingManager);
                        }
                        else {
                            // Should be no other values
                            message = null;
                        }
                    }
                } catch (UnknownHostException e) {
                    message = getResources().getString(R.string.error_unknown_host);
                } catch (FileNotFoundException e) {
                    message = getResources().getString(R.string.error_404);
                } catch (XmlPullParserException | IOException e) {
                    if (!pairingCancelled) e.printStackTrace();
                    // Sunshine commits the client certificate immediately before the final
                    // HTTPS confirmation. On fast automatic pairing, that confirmation can
                    // race certificate-chain publication even though pairing already succeeded.
                    // Recheck with the newly pinned server certificate before telling the user
                    // that pairing failed.
                    if (recoverCommittedPairing(httpConn, pairingManager, computer)) {
                        message = null;
                        success = true;
                    } else {
                        message = e.getMessage();
                    }
                } catch (RuntimeException invalidReply) {
                    LimeLog.warning("Pairing stopped: " + invalidReply.getClass().getSimpleName());
                    message = getString(R.string.pair_fail);
                }

                dismissPairingPinDialog();

                // The touch library checks file access in parallel with opening the desktop.
                // Other client layouts do not have that entry point.
                if (success && !UiHelper.isTouchscreenClient(PcView.this)) {
                    try {
                        FileTransferClient.bootstrap(PcView.this,
                                ServerHelper.getCurrentAddressFromComputer(computer).address, computer);
                    } catch (Exception transferError) {
                        LimeLog.warning("File transfer bootstrap after pairing failed: " +
                                transferError.getClass().getSimpleName());
                    }
                }

                final String toastMessage = message;
                if (!success && !pairingCancelled) DiagnosticReport.connection(PcView.this, "pair", "unknown", 0);
                final boolean toastSuccess = success;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        pairingInProgress = false;
                        if (pairingCancelled || isFinishing() || isDestroyed() || !inForeground) {
                            if (!isFinishing() && !isDestroyed()) startComputerUpdates();
                            return;
                        }
                        if (toastMessage != null) {
                            Toast.makeText(PcView.this, toastMessage, Toast.LENGTH_LONG).show();
                        }

                        if (toastSuccess) {
                            // Open the app list after a successful pairing attempt
                            doAppList(computer, true, false);
                        }
                        else {
                            // Start polling again if we're still in the foreground
                            startComputerUpdates();
                        }
                    }
                });
            }
        }).start();
    }

    private void persistPairingCertificate(ComputerDetails computer, PairingManager pairingManager) {
        ComputerManagerService.ComputerManagerBinder binder = managerBinder;
        ComputerDetails managedComputer = binder == null ? null : binder.getComputer(computer.uuid);
        if (managedComputer != null) {
            managedComputer.serverCert = pairingManager.getPairedCert();
            managedComputer.pairState = PairState.PAIRED;
            // Keep the object held by the click handler coherent until the next poll too.
            computer.serverCert = managedComputer.serverCert;
            computer.pairState = PairState.PAIRED;
            binder.persistPairedCertificate(computer.uuid);
        }
    }

    private boolean recoverCommittedPairing(NvHTTP httpConn, PairingManager pairingManager,
                                            ComputerDetails computer) {
        if (httpConn == null || pairingManager == null || pairingManager.getPairedCert() == null) {
            return false;
        }

        httpConn.setServerCert(pairingManager.getPairedCert());
        for (int attempt = 0; attempt < 3; attempt++) {
            try {
                if (attempt > 0) {
                    Thread.sleep(150L * attempt);
                }
                if (httpConn.getPairState() == PairState.PAIRED) {
                    persistPairingCertificate(computer, pairingManager);
                    return true;
                }
            } catch (IOException | XmlPullParserException ignored) {
                // The next bounded retry covers certificate publication and socket teardown.
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
        return false;
    }

    /**
     * Displays the pairing PIN as a dedicated, non-suppressible dialog. Waiting until onShow()
     * prevents a fast worker failure or lifecycle callback from racing the UI creation runnable.
     */
    private void showPairingPinDialogAndWait(final String pin, NvHTTP request) throws IOException {
        final CountDownLatch shown = new CountDownLatch(1);
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (pairingCancelled || isFinishing() || isDestroyed()) {
                    request.cancelPairing();
                    shown.countDown();
                    return;
                }

                dismissPairingPinDialogOnUiThread();
                pinConnection = request;
                pairingPinDialog = new android.app.AlertDialog.Builder(PcView.this)
                        .setTitle(getResources().getString(R.string.pair_pairing_title))
                        .setMessage(getResources().getString(R.string.pair_pairing_msg) + " " + pin + "\n\n" +
                                getResources().getString(R.string.pair_pairing_help))
                        .setNegativeButton(android.R.string.cancel, (dialog, which) -> cancelPinPairing())
                        .setOnCancelListener(dialog -> cancelPinPairing())
                        .create();
                pairingPinDialog.setCanceledOnTouchOutside(false);
                pairingPinDialog.setOnShowListener(dialog -> shown.countDown());
                pairingPinDialog.show();
            }
        });

        try {
            if (!shown.await(5, TimeUnit.SECONDS)) {
                throw new IOException("Pairing PIN window could not be displayed");
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new IOException("Pairing was interrupted before the PIN window appeared", e);
        }
    }

    private void cancelPinPairing() {
        pairingCancelled = true;
        if (pinConnection != null) pinConnection.cancelPairing();
    }

    private void dismissPairingPinDialog() {
        runOnUiThread(this::dismissPairingPinDialogOnUiThread);
    }

    private void dismissPairingPinDialogOnUiThread() {
        pinConnection = null;
        if (pairingPinDialog != null) {
            pairingPinDialog.setOnShowListener(null);
            if (pairingPinDialog.isShowing()) {
                pairingPinDialog.dismiss();
            }
            pairingPinDialog = null;
        }
    }

    private void doWakeOnLan(final ComputerDetails computer) {
        if (computer.state == ComputerDetails.State.ONLINE) {
            Toast.makeText(PcView.this, getResources().getString(R.string.wol_pc_online), Toast.LENGTH_SHORT).show();
            return;
        }

        if (computer.macAddress == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.wol_no_mac), Toast.LENGTH_SHORT).show();
            return;
        }

        new Thread(new Runnable() {
            @Override
            public void run() {
                String message;
                try {
                    WakeOnLanSender.sendWolPacket(computer);
                    message = getResources().getString(R.string.wol_waking_msg);
                } catch (IOException e) {
                    message = getResources().getString(R.string.wol_fail);
                }

                final String toastMessage = message;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        Toast.makeText(PcView.this, toastMessage, Toast.LENGTH_LONG).show();
                    }
                });
            }
        }).start();
    }

    private void retryComputer(final ComputerDetails computer) {
        if (managerBinder == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
            return;
        }

        managerBinder.invalidateStateForComputer(computer.uuid);
        startComputerUpdates();
        Toast.makeText(PcView.this, R.string.connection_checking_again, Toast.LENGTH_SHORT).show();
    }

    private void showConnectionAssistant(final ComputerDetails computer) {
        if (connectionAssistantDialog != null) {
            connectionAssistantDialog.dismiss();
        }

        connectionAssistantDialog = ConnectionAssistantDialog.show(this, computer,
                new ConnectionAssistantDialog.Listener() {
                    @Override
                    public void onRetry() {
                        retryComputer(computer);
                    }

                    @Override
                    public void onWake() {
                        doWakeOnLan(computer);
                    }

                    @Override
                    public void onPair() {
                        doPair(computer);
                    }

                    @Override
                    public void onOpenLibrary() {
                        doAppList(computer, false, false);
                    }

                    @Override
                    public void onOpenPrivateNetwork() {
                        TailscaleLauncher.openAppOrPlayStore(PcView.this);
                    }

                    @Override
                    public void onNetworkTest() {
                        ServerHelper.doNetworkTest(PcView.this);
                    }
                });
    }

    private void doUnpair(final ComputerDetails computer) {
        if (computer.state == ComputerDetails.State.OFFLINE || computer.activeAddress == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.error_pc_offline), Toast.LENGTH_SHORT).show();
            return;
        }
        if (managerBinder == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
            return;
        }

        Toast.makeText(PcView.this, getResources().getString(R.string.unpairing), Toast.LENGTH_SHORT).show();
        new Thread(new Runnable() {
            @Override
            public void run() {
                NvHTTP httpConn;
                String message;
                try {
                    httpConn = new NvHTTP(ServerHelper.getCurrentAddressFromComputer(computer),
                            computer.httpsPort, managerBinder.getUniqueId(), computer.serverCert,
                            PlatformBinding.getCryptoProvider(PcView.this));
                    NetHelper.bindToDirectLocalNetwork(PcView.this, httpConn,
                            ServerHelper.getCurrentAddressFromComputer(computer).address);
                    if (httpConn.getPairState() == PairingManager.PairState.PAIRED) {
                        httpConn.unpair();
                        if (httpConn.getPairState() == PairingManager.PairState.NOT_PAIRED) {
                            message = getResources().getString(R.string.unpair_success);
                        }
                        else {
                            message = getResources().getString(R.string.unpair_fail);
                        }
                    }
                    else {
                        message = getResources().getString(R.string.unpair_error);
                    }
                } catch (UnknownHostException e) {
                    message = getResources().getString(R.string.error_unknown_host);
                } catch (FileNotFoundException e) {
                    message = getResources().getString(R.string.error_404);
                } catch (XmlPullParserException | IOException e) {
                    message = e.getMessage();
                    e.printStackTrace();
                }

                final String toastMessage = message;
                runOnUiThread(new Runnable() {
                    @Override
                    public void run() {
                        Toast.makeText(PcView.this, toastMessage, Toast.LENGTH_LONG).show();
                    }
                });
            }
        }).start();
    }

    private void doAppList(ComputerDetails computer, boolean newlyPaired, boolean showHiddenGames) {
        doAppList(computer, newlyPaired, showHiddenGames, false);
    }

    private void doAppList(ComputerDetails computer, boolean newlyPaired, boolean showHiddenGames, boolean openDesktop) {
        doAppList(computer, newlyPaired, showHiddenGames, openDesktop, 0);
    }

    private void doAppList(ComputerDetails computer, boolean newlyPaired, boolean showHiddenGames,
                           boolean openDesktop, int tvAppId) {
        doAppList(computer, newlyPaired, showHiddenGames, openDesktop, tvAppId, false);
    }

    private void doAppList(ComputerDetails computer, boolean newlyPaired, boolean showHiddenGames,
                           boolean openDesktop, int tvAppId, boolean superScreen) {
        if (openingLibrary) return;
        if (computer.state == ComputerDetails.State.OFFLINE) {
            Toast.makeText(PcView.this, getResources().getString(R.string.error_pc_offline), Toast.LENGTH_SHORT).show();
            return;
        }
        if (managerBinder == null) {
            Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
            return;
        }

        Intent i = new Intent(this, AppView.class);
        i.putExtra(AppView.NAME_EXTRA, computer.name);
        i.putExtra(AppView.UUID_EXTRA, computer.uuid);
        i.putExtra(AppView.NEW_PAIR_EXTRA, newlyPaired);
        i.putExtra(AppView.SHOW_HIDDEN_APPS_EXTRA, showHiddenGames);
        if (tvAppId > 0) i.putExtra(AppView.TV_CONTINUE_ID, tvAppId);
        if (openDesktop) {
            i.putExtra(AppView.OPEN_DESKTOP, true);
            i.putExtra(Game.EXTRA_SUPER_SCREEN, superScreen);
            i.putExtra(AppView.DESKTOP_TAP_TIME, android.os.SystemClock.elapsedRealtime());
        }
        openingLibrary = true;
        try { startActivity(i); }
        catch (RuntimeException error) { openingLibrary = false; throw error; }
    }

    private boolean handleComputerAction(MenuItem item, ComputerObject computer, int runningId) {
        switch (item.getItemId()) {
            case AUTO_MIC_ID:
                Game.setAutoMicrophone(this, computer.details.uuid, !item.isChecked());
                return true;
            case CONNECTION_METHODS_ID:
                openRoutePicker(computer.details);
                return true;
            case TERMINAL_ID:
                openTerminal(computer.details);
                return true;
            case PAIR_ID:
                doPair(computer.details);
                return true;

            case UNPAIR_ID:
                doUnpair(computer.details);
                return true;

            case WOL_ID:
                doWakeOnLan(computer.details);
                return true;

            case DELETE_ID:
                if (ActivityManager.isUserAMonkey()) {
                    LimeLog.info("Ignoring delete PC request from monkey");
                    return true;
                }
                UiHelper.displayDeletePcConfirmationDialog(this, computer.details, new Runnable() {
                    @Override
                    public void run() {
                        if (managerBinder == null) {
                            Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
                            return;
                        }
                        removeComputer(computer.details);
                    }
                }, null);
                return true;

            case FULL_APP_LIST_ID:
                doAppList(computer.details, false, true);
                return true;

            case RESUME_ID:
                if (managerBinder == null) {
                    Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
                    return true;
                }

                ServerHelper.doStart(this, new NvApp("app", runningId, false), computer.details, managerBinder);
                return true;

            case QUIT_ID:
                if (managerBinder == null) {
                    Toast.makeText(PcView.this, getResources().getString(R.string.error_manager_not_running), Toast.LENGTH_LONG).show();
                    return true;
                }

                // Display a confirmation dialog first
                UiHelper.displayQuitConfirmationDialog(this, new Runnable() {
                    @Override
                    public void run() {
                        ServerHelper.doQuit(PcView.this, computer.details,
                                new NvApp("app", runningId, false), managerBinder, null);
                    }
                }, null);
                return true;

            case VIEW_DETAILS_ID:
                Dialog.displayDialog(PcView.this, getResources().getString(R.string.title_details), computer.details.toString(), false);
                return true;

            case TEST_NETWORK_ID:
                showConnectionAssistant(computer.details);
                return true;

            case GAMESTREAM_EOL_ID:
                HelpLauncher.launchGameStreamEolFaq(PcView.this);
                return true;

            default:
                return false;
        }
    }
    
    private void removeComputer(ComputerDetails details) {
        managerBinder.removeComputer(details);

        new DiskAssetLoader(this).deleteAssetsForComputer(details.uuid);

        // Delete hidden games preference value
        getSharedPreferences(AppView.HIDDEN_APPS_PREF_FILENAME, MODE_PRIVATE)
                .edit()
                .remove(details.uuid)
                .apply();

        for (int i = 0; i < pcGridAdapter.getCount(); i++) {
            ComputerObject computer = (ComputerObject) pcGridAdapter.getItem(i);

            if (details.equals(computer.details)) {
                // Disable or delete shortcuts referencing this PC
                shortcutHelper.disableComputerShortcut(details,
                        getResources().getString(R.string.scut_deleted_pc));

                pcGridAdapter.removeComputer(computer);
                if (tvContinue != null) {
                    TvLastSession.forget(this, details.uuid);
                    refreshTvContinue();
                }
                pcGridAdapter.notifyDataSetChanged();

                if (pcGridAdapter.getCount() == 0) {
                    // Show the "Discovery in progress" view
                    noPcFoundLayout.setVisibility(View.VISIBLE);
                }

                break;
            }
        }
    }
    
    private void updateComputer(ComputerDetails details) {
        if (details.state == ComputerDetails.State.ONLINE && details.pairState == PairState.PAIRED && details.serverCert != null &&
                details.activeAddress != null && com.limelight.piulink.NetworkPathClassifier.classify(
                    details.activeAddress.address) == com.limelight.piulink.NetworkPathClassifier.Path.TAILSCALE_MANAGED) {
            String address = details.activeAddress.address;
            if (!address.equals(warmedDisplayRoutes.put(details.uuid, address))) {
                com.limelight.piulink.MonitorSelection.warmInventory(this, address);
                if (managerBinder != null) managerBinder.warmDesktopEntry(details);
            }
        } else {
            warmedDisplayRoutes.remove(details.uuid);
        }
        ComputerObject existingEntry = null;

        for (int i = 0; i < pcGridAdapter.getCount(); i++) {
            ComputerObject computer = (ComputerObject) pcGridAdapter.getItem(i);

            // Check if this is the same computer
            if (details.uuid.equals(computer.details.uuid)) {
                existingEntry = computer;
                break;
            }
        }

        if (existingEntry != null) {
            // Replace the information in the existing entry
            existingEntry.details = details;
        }
        else {
            // Add a new entry
            pcGridAdapter.addComputer(new ComputerObject(details));

            // Remove the "Discovery in progress" view
            noPcFoundLayout.setVisibility(View.INVISIBLE);
        }

        // Notify the view that the data has changed
        pcGridAdapter.notifyDataSetChanged();
        refreshTvContinue();
    }

    private void refreshTvContinue() {
        if (tvContinue == null) return;
        TvLastSession last = TvLastSession.read(this);
        ComputerDetails target = null;
        for (int i = 0; i < pcGridAdapter.getCount(); i++) {
            ComputerDetails pc = ((ComputerObject) pcGridAdapter.getItem(i)).details;
            if (TvLastSession.isHomeHost(last.host, pc.uuid, pcGridAdapter.getCount())) {
                target = pc;
                break;
            }
        }
        if (target == null) {
            tvContinue.setVisibility(View.GONE);
            return;
        }
        final ComputerDetails pc = target;
        boolean newlyVisible = tvContinue.getVisibility() != View.VISIBLE;
        TvLastSession.HomeAction action = TvLastSession.homeAction(pc.state, pc.serverCert != null, pc.pairState);
        String label;
        if (action == TvLastSession.HomeAction.CONTINUE) {
            java.util.List<Integer> previous = last.appId > 0 ? java.util.Collections.emptyList()
                    : AppView.readRecentApps(this, pc.uuid);
            int appId = previous.isEmpty() ? last.appId : Math.max(0, previous.get(0));
            label = appId > 0 ? getString(R.string.tv_continue_app,
                    last.appName.isEmpty() ? getString(R.string.tv_last_app) : last.appName, pc.name)
                    : getString(R.string.tv_open_desktop, pc.name);
            tvContinue.setOnClickListener(v -> doAppList(pc, false, false, appId == 0, appId));
        } else {
            int text;
            switch (action) {
                case OFFLINE: text = R.string.tv_host_offline; break;
                case PAIR: text = R.string.tv_host_pairing; break;
                default: text = R.string.tv_host_checking; break;
            }
            label = getString(text, pc.name);
            tvContinue.setOnClickListener(v -> showConnectionAssistant(pc));
        }
        if (!android.text.TextUtils.equals(label, tvContinue.getText())) tvContinue.setText(label);
        tvContinue.setVisibility(View.VISIBLE);
        if (newlyVisible && !homeInteracted && tvContinue.isShown()) tvContinue.requestFocus();
    }

    @Override
    public int getAdapterFragmentLayoutId() {
        return R.layout.pc_grid_view;
    }

    @Override
    public void receiveAbsListView(AbsListView listView) {
        if (UiHelper.isTouchscreenClient(this)) {
            ((android.widget.GridView) listView).setNumColumns(1);
            listView.setPadding(0, 0, 0, listView.getPaddingBottom());
        }
        listView.setAdapter(pcGridAdapter);
        listView.setOnItemClickListener(new OnItemClickListener() {
            @Override
            public void onItemClick(AdapterView<?> arg0, View arg1, int pos,
                                    long id) {
                ComputerObject computer = (ComputerObject) pcGridAdapter.getItem(pos);
                if (UiHelper.isTouchscreenClient(PcView.this)) autoOpenDesktop(computer.details);
                else openHost(computer.details);
            }
        });
        UiHelper.applyStatusBarPadding(listView);
        registerForContextMenu(listView);
    }

    private void openHost(ComputerDetails computer) {
        if (computer.state != ComputerDetails.State.ONLINE) {
            showConnectionAssistant(computer);
        } else if (computer.pairState != PairState.PAIRED) {
            doPair(computer);
        } else {
            // A persisted client certificate is the durable proof of pairing. Do not run
            // the pairing protocol merely to open a saved Host: a transient HTTPS probe
            // failure would fall back to trusted auto-pair and show "Pairing" on every
            // application restart. Authenticated app-list/launch requests remain protected
            // by this pin; the explicit Pair action is the repair path after a Host reset.
            doAppList(computer, false, false);
        }
    }

    private void openTerminal(ComputerDetails computer) {
        ComputerDetails.AddressTuple address = computer.activeAddress != null ? computer.activeAddress :
            computer.manualAddress != null ? computer.manualAddress : computer.localAddress != null ?
            computer.localAddress : computer.remoteAddress;
        Intent intent = new Intent(this, com.limelight.piulink.ssh.TerminalHub.class);
        if (address != null) intent.putExtra("host", address.address);
        startActivity(intent);
    }

    public static class ComputerObject {
        public ComputerDetails details;

        public ComputerObject(ComputerDetails details) {
            if (details == null) {
                throw new IllegalArgumentException("details must not be null");
            }
            this.details = details;
        }

        @Override
        public String toString() {
            return details.name;
        }
    }
}
