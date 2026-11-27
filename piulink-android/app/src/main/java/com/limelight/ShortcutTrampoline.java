package com.limelight;

import android.app.Activity;
import android.app.Service;
import android.content.ComponentName;
import android.content.Intent;
import android.content.ServiceConnection;
import android.os.Bundle;
import android.os.IBinder;

import com.limelight.computers.ComputerDatabaseManager;
import com.limelight.computers.ComputerManagerService;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.nvstream.wol.WakeOnLanSender;
import com.limelight.utils.CacheHelper;
import com.limelight.utils.Dialog;
import com.limelight.utils.SpinnerDialog;
import com.limelight.utils.UiHelper;

import org.xmlpull.v1.XmlPullParserException;

import java.io.IOException;
import java.io.StringReader;
import java.util.List;
import java.util.UUID;

public class ShortcutTrampoline extends Activity {
    private String uuidString;
    private NvApp app;
    private int wakeHostTries = 10;
    private boolean serviceBound;
    private boolean handled;
    private boolean polling;
    private SpinnerDialog blockingLoadSpinner;
    private volatile ComputerManagerService.ComputerManagerBinder managerBinder;

    private final ServiceConnection serviceConnection = new ServiceConnection() {
        public void onServiceConnected(ComponentName name, IBinder service) {
            ComputerManagerService.ComputerManagerBinder binder =
                    (ComputerManagerService.ComputerManagerBinder) service;
            managerBinder = binder;
            new Thread(() -> {
                binder.waitForReady();
                ComputerDetails saved = binder.getComputer(uuidString);
                runOnUiThread(() -> {
                    if (managerBinder != binder || handled || isFinishing()) return;
                    if (saved == null) {
                        finishWithError(R.string.scut_pc_not_found);
                        return;
                    }
                    binder.invalidateStateForComputer(saved.uuid);
                    polling = true;
                    binder.startPolling(details -> {
                        if (managerBinder != binder || !saved.uuid.equalsIgnoreCase(details.uuid)) return;
                        if (details.state == ComputerDetails.State.OFFLINE && details.macAddress != null && wakeHostTries-- > 0) {
                            new Thread(() -> {
                                try {
                                    WakeOnLanSender.sendWolPacket(details);
                                    if (managerBinder == binder) binder.invalidateStateForComputer(saved.uuid);
                                } catch (IOException ignored) {
                                    runOnUiThread(() -> openLibrary(binder, details));
                                }
                            }, "PiuLink shortcut wake").start();
                            return;
                        }
                        if (details.state != ComputerDetails.State.UNKNOWN) {
                            runOnUiThread(() -> openLibrary(binder, details));
                        }
                    });
                });
            }, "PiuLink shortcut").start();
        }

        public void onServiceDisconnected(ComponentName name) {
            managerBinder = null;
            if (!handled && !isFinishing()) finishWithError(R.string.error_manager_not_running);
        }
    };

    private void finishWithError(int message) {
        handled = true;
        if (blockingLoadSpinner != null) blockingLoadSpinner.dismiss();
        Dialog.displayDialog(this, R.string.conn_error_title, getString(message), true);
    }

    private void openLibrary(ComputerManagerService.ComputerManagerBinder binder, ComputerDetails details) {
        if (handled || managerBinder != binder || isFinishing()) return;
        handled = true;
        if (polling) { polling = false; binder.stopPolling(); }
        if (details.state != ComputerDetails.State.ONLINE) {
            finishWithError(R.string.error_pc_offline);
            return;
        }
        if (details.pairState != PairingManager.PairState.PAIRED) {
            finishWithError(R.string.scut_not_paired);
            return;
        }

        Intent library = new Intent(this, AppView.class)
                .putExtra(AppView.UUID_EXTRA, details.uuid)
                .putExtra(AppView.NAME_EXTRA, details.name);
        if (app != null) {
            library.putExtra(Game.EXTRA_APP_ID, app.getAppId())
                    .putExtra(Game.EXTRA_APP_NAME, app.getAppName())
                    .putExtra(Game.EXTRA_APP_HDR, app.isHdrSupported());
            startActivity(library);
        } else {
            library.putExtra(AppView.SHORTCUT_RESUME_ID, details.runningGameId);
            Intent home = new Intent(this, PcView.class).setAction(Intent.ACTION_MAIN)
                    .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TASK | Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivities(new Intent[]{home, library});
        }
        finish();
    }

    protected boolean validateInput(String uuidString, String appIdString, String nameString) {
        // Validate PC UUID/Name
        if (uuidString == null && nameString == null) {
            Dialog.displayDialog(ShortcutTrampoline.this,
                    R.string.conn_error_title,
                    getResources().getString(R.string.scut_invalid_uuid),
                    true);
            return false;
        }

        if (uuidString != null && !uuidString.isEmpty()) {
            try {
                UUID.fromString(uuidString);
            } catch (IllegalArgumentException ex) {
                Dialog.displayDialog(ShortcutTrampoline.this,
                        R.string.conn_error_title,
                        getResources().getString(R.string.scut_invalid_uuid),
                        true);
                return false;
            }
        } else {
            // UUID is null, so fallback to Name
            if (nameString == null || nameString.isEmpty()) {
                Dialog.displayDialog(ShortcutTrampoline.this,
                        R.string.conn_error_title,
                        getResources().getString(R.string.scut_invalid_uuid),
                        true);
                return false;
            }
        }

        // Validate App ID (if provided)
        if (appIdString != null && !appIdString.isEmpty()) {
            try {
                if (Integer.parseInt(appIdString) <= 0) throw new NumberFormatException();
            } catch (NumberFormatException ex) {
                Dialog.displayDialog(ShortcutTrampoline.this,
                        R.string.conn_error_title,
                        getResources().getString(R.string.scut_invalid_app_id),
                        true);
                return false;
            }
        }

        return true;
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        UiHelper.notifyNewRootView(this);
        ComputerDetails _computer = null;

        // PC arguments, both are optional, but at least one must be provided
        uuidString = getIntent().getStringExtra(AppView.UUID_EXTRA);
        String nameString = getIntent().getStringExtra(AppView.NAME_EXTRA);

        // App arguments, both are optional, but one must be provided in order to start an app
        String appIdString = getIntent().getStringExtra(Game.EXTRA_APP_ID);
        String appNameString = getIntent().getStringExtra(Game.EXTRA_APP_NAME);

        if (!validateInput(uuidString, appIdString, nameString)) {
            // Invalid input, so just return
            return;
        }

        if (uuidString == null || uuidString.isEmpty()) {
            // Use nameString to find the corresponding UUID
            ComputerDatabaseManager dbManager = new ComputerDatabaseManager(this);
            try { _computer = dbManager.getComputerByName(nameString); }
            finally { dbManager.close(); }

            if (_computer == null) {
                Dialog.displayDialog(ShortcutTrampoline.this,
                        R.string.conn_error_title,
                        getResources().getString(R.string.scut_pc_not_found),
                        true);
                return;
            }

            uuidString = _computer.uuid;

            // Set the AppView UUID intent, since it wasn't provided
            setIntent(new Intent(getIntent()).putExtra(AppView.UUID_EXTRA, uuidString));
        }

        if (appIdString != null && !appIdString.isEmpty()) {
            app = new NvApp(getIntent().getStringExtra(Game.EXTRA_APP_NAME),
                    Integer.parseInt(appIdString),
                    getIntent().getBooleanExtra(Game.EXTRA_APP_HDR, false));
        }
        else if (appNameString != null && !appNameString.isEmpty()) {
            // Use appNameString to find the corresponding AppId
            try {
                int appId = -1;
                String rawAppList = CacheHelper.readInputStreamToString(CacheHelper.openCacheFileForInput(getCacheDir(), "applist", uuidString));

                if (rawAppList.isEmpty()) {
                    Dialog.displayDialog(ShortcutTrampoline.this,
                            R.string.conn_error_title,
                            getResources().getString(R.string.scut_invalid_app_id),
                            true);
                    return;
                }
                List<NvApp> applist = NvHTTP.getAppListByReader(new StringReader(rawAppList));

                for (NvApp _app : applist) {
                    if (_app.getAppName().equals(appNameString)) {
                        appId = _app.getAppId();
                        break;
                    }
                }
                if (appId < 0) {
                    Dialog.displayDialog(ShortcutTrampoline.this,
                            R.string.conn_error_title,
                            getResources().getString(R.string.scut_invalid_app_id),
                            true);
                    return;
                }
                setIntent(new Intent(getIntent()).putExtra(Game.EXTRA_APP_ID, appId));
                app = new NvApp(
                        appNameString,
                        appId,
                        getIntent().getBooleanExtra(Game.EXTRA_APP_HDR, false));
            } catch (IOException | XmlPullParserException e) {
                Dialog.displayDialog(ShortcutTrampoline.this,
                        R.string.conn_error_title,
                        getResources().getString(R.string.scut_invalid_app_id),
                        true);
                return;
            }
        }

        // Bind to the computer manager service
        serviceBound = bindService(new Intent(this, ComputerManagerService.class), serviceConnection,
                Service.BIND_AUTO_CREATE);
        if (!serviceBound) { finishWithError(R.string.error_manager_not_running); return; }

        blockingLoadSpinner = SpinnerDialog.displayDialog(this, R.string.conn_establishing_title,
                getResources().getString(R.string.applist_connect_msg), true);
    }

    @Override
    protected void onStop() {
        super.onStop();

        if (blockingLoadSpinner != null) {
            blockingLoadSpinner.dismiss();
            blockingLoadSpinner = null;
        }

        Dialog.closeDialogs(this);

        handled = true;
        if (polling && managerBinder != null) managerBinder.stopPolling();
        polling = false;
        managerBinder = null;
        if (serviceBound) {
            serviceBound = false;
            unbindService(serviceConnection);
        }

        finish();
    }
}
