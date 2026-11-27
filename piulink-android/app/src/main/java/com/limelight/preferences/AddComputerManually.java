package com.limelight.preferences;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.URI;
import java.net.URISyntaxException;
import java.util.Collections;

import com.limelight.utils.NetHelper;
import com.limelight.computers.ComputerManagerService;
import com.limelight.R;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.jni.MoonBridge;
import com.limelight.utils.Dialog;
import com.limelight.utils.ServerHelper;
import android.app.ProgressDialog;
import com.limelight.utils.TailscaleLauncher;
import com.limelight.utils.UiHelper;

import android.app.Activity;
import android.app.Service;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.os.Bundle;
import android.os.IBinder;
import android.view.KeyEvent;
import android.view.View;
import android.view.inputmethod.EditorInfo;
import android.view.inputmethod.InputMethodManager;
import android.widget.TextView;
import android.widget.Toast;

public class AddComputerManually extends Activity {
    public static final String EXTRA_AUTO_ADD_HOST = "labs.atipiu.piulink.AUTO_ADD_HOST";
    public static final String EXTRA_ROUTE_HOST = "labs.atipiu.piulink.ROUTE_HOST";
    public static final String EXTRA_ROUTE_KEY = "labs.atipiu.piulink.ROUTE_KEY";
    private TextView hostText;
    private ComputerManagerService.ComputerManagerBinder managerBinder;
    private Thread addThread;
    private ProgressDialog progress;
    private boolean serviceBound;
    private boolean visible;
    private String autoAddHost;
    private URI pendingAddress;
    private String pendingHostId;
    private boolean forcePunch;
    private final ServiceConnection serviceConnection = new ServiceConnection() {
        public void onServiceConnected(ComponentName name, IBinder binder) {
            managerBinder = (ComputerManagerService.ComputerManagerBinder) binder;
            startSavedRoute();
            startPendingAdd();
        }

        public void onServiceDisconnected(ComponentName name) {
            managerBinder = null;
            cancelAdd();
            if (visible && !isFinishing()) hostText.setError(getString(R.string.error_manager_not_running));
        }
    };

    private boolean isWrongSubnetSiteLocalAddress(String address) {
        try {
            InetAddress targetAddress = InetAddress.getByName(address);
            if (!(targetAddress instanceof Inet4Address) || !targetAddress.isSiteLocalAddress()) {
                return false;
            }

            // We have a site-local address. Look for a matching local interface.
            for (NetworkInterface iface : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                for (InterfaceAddress addr : iface.getInterfaceAddresses()) {
                    if (!(addr.getAddress() instanceof Inet4Address) || !addr.getAddress().isSiteLocalAddress()) {
                        // Skip non-site-local or non-IPv4 addresses
                        continue;
                    }

                    if (matchesPrefix(targetAddress.getAddress(), addr.getAddress().getAddress(),
                            addr.getNetworkPrefixLength())) {
                        return false;
                    }
                }
            }

            // Couldn't find a matching interface
            return true;
        } catch (Exception e) {
            // Catch all exceptions because some broken Android devices
            // will throw an NPE from inside getNetworkInterfaces().
            e.printStackTrace();
            return false;
        }
    }

    static boolean matchesPrefix(byte[] left, byte[] right, int bits) {
        if (left.length != right.length || bits < 0 || bits > left.length * 8) return false;
        for (int i = 0; bits > 0; i++, bits -= 8) {
            int mask = 0xff << (8 - Math.min(8, bits));
            if (((left[i] ^ right[i]) & mask) != 0) return false;
        }
        return true;
    }

    static URI parseRawUserInputToUri(String raw) {
        // Chinese keyboards may insert an ideographic stop or full-width port separator.
        String value = raw.trim().replace('。', '.').replace('．', '.').replace('｡', '.').replace('：', ':');
        if (value.isEmpty() || value.contains("/") || value.contains("?") || value.contains("#") || value.contains("@")) return null;
        // A bare IPv6 literal has no port. A custom port requires brackets.
        if (!value.startsWith("[") && value.indexOf(':') != value.lastIndexOf(':')) value = "[" + value + "]";
        if (value.endsWith(":")) return null;
        try {
            URI uri = new URI("moonlight://" + value);
            int port = uri.getPort();
            return uri.getHost() != null && (port == -1 || (port >= 1 && port <= 65535)) ? uri : null;
        } catch (URISyntaxException invalid) {
            return null;
        }
    }

    private void submitAddress(String raw) {
        submitAddress(raw, false);
    }

    private void submitAddress(String raw, boolean punch) {
        submitAddress(raw, punch, null);
    }

    private void submitAddress(String raw, boolean punch, String expectedHostId) {
        if (pendingAddress != null || addThread != null) return;
        URI parsed = com.limelight.computers.HostInvite.parseUri(raw.trim());
        if (parsed == null && !punch) parsed = parseRawUserInputToUri(raw);
        if (parsed == null) {
            hostText.setError(getString(raw.trim().isEmpty() ? R.string.connect_input_empty : R.string.connect_input_invalid));
            hostText.requestFocus();
            return;
        }
        if ("https".equals(parsed.getScheme()) && com.limelight.piulink.InviteVpnChoice.offer(this,
                () -> submitAddress(raw, punch, expectedHostId))) return;
        hostText.setError(null);
        pendingAddress = parsed;
        pendingHostId = expectedHostId;
        forcePunch = punch;
        progress = new ProgressDialog(this);
        progress.setTitle(R.string.title_add_pc);
        progress.setMessage(getString(R.string.msg_add_pc));
        progress.setCanceledOnTouchOutside(false);
        progress.setButton(ProgressDialog.BUTTON_NEGATIVE, getText(android.R.string.cancel), (d, w) -> cancelAdd());
        progress.setOnCancelListener(d -> cancelAdd());
        com.limelight.utils.TouchDialog.prepareHeader(progress, getText(R.string.title_add_pc));
        progress.show();
        startPendingAdd();
    }

    private void startPendingAdd() {
        if (!visible || managerBinder == null || pendingAddress == null || addThread != null) return;
        URI address = pendingAddress;
        boolean punch = forcePunch;
        String expectedHostId = pendingHostId;
        pendingAddress = null;
        ComputerManagerService.ComputerManagerBinder binder = managerBinder;
        addThread = new Thread(() -> {
            Thread request = Thread.currentThread();
            int result;
            try {
                result = addPc(binder, address, punch, expectedHostId);
            } catch (InterruptedException cancelled) {
                return;
            } catch (IllegalArgumentException invalid) {
                result = R.string.addpc_invalid_address;
            } catch (RuntimeException unavailable) {
                result = R.string.addpc_fail;
            }
            if (result != R.string.addpc_success && !request.isInterrupted()) {
                boolean invite = "https".equals(address.getScheme());
                String stage = result == R.string.invite_no_route || punch ? "p2p" : invite ? "signal" : "host";
                String reason = result == R.string.invite_offline ? "offline" :
                        result == R.string.addpc_invalid_address ? "invalid" : "unreachable";
                com.limelight.DiagnosticReport.connection(this, stage, reason, 0);
            }
            final int message = result;
            runOnUiThread(() -> {
                if (addThread != request || !visible || isFinishing()) return;
                addThread = null;
                dismissProgress();
                if (message == R.string.addpc_success) {
                    if (expectedHostId != null) setResult(RESULT_OK, new Intent().putExtra(EXTRA_ROUTE_HOST, expectedHostId));
                    else {
                        setResult(RESULT_OK);
                        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
                    }
                    finish();
                } else if (expectedHostId != null && getIntent().hasExtra(EXTRA_ROUTE_HOST)) {
                    Dialog.displayDialog(this, getString(R.string.conn_error_title), getString(message), () -> {
                        if (visible && managerBinder != null && !isFinishing()) {
                            ConnectionRoutePicker.show(this, managerBinder, expectedHostId, this::connectRoute);
                        }
                    });
                } else {
                    Dialog.displayDialog(this, getString(R.string.conn_error_title), getString(message), false);
                }
            });
        }, "PiuLink add computer");
        addThread.start();
    }

    private int addPc(ComputerManagerService.ComputerManagerBinder binder, URI address, boolean punch, String expectedHostId) throws InterruptedException {
        if ("https".equals(address.getScheme())) {
            try { return binder.addInvite(address.toString(), punch, expectedHostId) ? R.string.addpc_success : R.string.invite_no_route; }
            catch (java.io.FileNotFoundException offline) { return R.string.invite_offline; }
            catch (java.io.IOException unavailable) { return R.string.invite_unavailable; }
        }
        String host = address.getHost();
        if (host.startsWith("[") && host.endsWith("]")) host = host.substring(1, host.length() - 1);
        ComputerDetails details = new ComputerDetails();
        details.manualAddress = new ComputerDetails.AddressTuple(host,
                address.getPort() == -1 ? NvHTTP.DEFAULT_HTTP_PORT : address.getPort());
        if (expectedHostId != null) return binder.addHistoricalAddress(expectedHostId, details.manualAddress) ? R.string.addpc_success : R.string.addpc_fail;
        if (binder.addComputerBlocking(details)) return R.string.addpc_success;
        if (Thread.currentThread().isInterrupted()) throw new InterruptedException();
        boolean vpn = NetHelper.isActiveNetworkVpn(this);
        if (!vpn && isWrongSubnetSiteLocalAddress(host)) return R.string.addpc_wrong_sitelocal;
        if (!vpn && !NetHelper.isPrivateHostAddress(host)) {
            int ports = MoonBridge.testClientConnectivity(ServerHelper.CONNECTION_TEST_SERVER, 443,
                    MoonBridge.ML_PORT_FLAG_TCP_47984 | MoonBridge.ML_PORT_FLAG_TCP_47989);
            if (ports != MoonBridge.ML_TEST_RESULT_INCONCLUSIVE && ports != 0) return R.string.nettest_text_blocked;
        }
        return R.string.addpc_fail;
    }

    private void dismissProgress() {
        if (progress != null) { progress.dismiss(); progress = null; }
    }

    private void cancelAdd() {
        pendingAddress = null;
        if (addThread != null) {
            addThread.interrupt();
            addThread = null;
        }
        dismissProgress();
    }

    @Override protected void onStart() {
        super.onStart();
        visible = true;
        startSavedRoute();
        if (serviceBound && autoAddHost != null && !autoAddHost.isEmpty()) {
            String initial = autoAddHost;
            autoAddHost = null;
            submitAddress(initial);
        }
    }

    @Override protected void onStop() {
        visible = false;
        cancelAdd();
        Dialog.closeDialogs(this);
        super.onStop();
    }

    @Override protected void onDestroy() {
        if (serviceBound) unbindService(serviceConnection);
        super.onDestroy();
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        UiHelper.setLocale(this);

        setContentView(R.layout.activity_add_computer_manually);

        UiHelper.notifyNewRootView(this);

        this.hostText = findViewById(R.id.hostTextView);
        autoAddHost = getIntent().getStringExtra(EXTRA_AUTO_ADD_HOST);
        if (autoAddHost != null && !autoAddHost.isEmpty()) {
            hostText.setText(autoAddHost);
        }
        hostText.setImeOptions(EditorInfo.IME_ACTION_DONE);
        hostText.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override
            public boolean onEditorAction(TextView textView, int actionId, KeyEvent keyEvent) {
                if (actionId == EditorInfo.IME_ACTION_DONE ||
                        (keyEvent != null &&
                                keyEvent.getAction() == KeyEvent.ACTION_DOWN &&
                                keyEvent.getKeyCode() == KeyEvent.KEYCODE_ENTER)) {
                    return handleDoneEvent();
                }
                else if (actionId == EditorInfo.IME_ACTION_PREVIOUS) {
                    // This is how the Fire TV dismisses the keyboard
                    InputMethodManager imm = (InputMethodManager)getSystemService(Context.INPUT_METHOD_SERVICE);
                    imm.hideSoftInputFromWindow(hostText.getWindowToken(), 0);
                    return false;
                }

                return false;
            }
        });

        findViewById(R.id.addPcButton).setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                handleDoneEvent();
            }
        });
        findViewById(R.id.historyButton).setOnClickListener(view -> showHistory());
        View punch = findViewById(R.id.punchButton);
        punch.setOnClickListener(view -> submitAddress(hostText.getText().toString(), true));
        hostText.addTextChangedListener(new android.text.TextWatcher() {
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
            public void afterTextChanged(android.text.Editable s) {}
            public void onTextChanged(CharSequence s, int start, int before, int count) {
                updateInviteAction(punch);
            }
        });
        updateInviteAction(punch);
        findViewById(R.id.addPcBackButton).setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                finish();
            }
        });
        findViewById(R.id.addPcPrivateNetworkButton).setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                TailscaleLauncher.openAppOrPlayStore(AddComputerManually.this);
            }
        });

        // Bind to the ComputerManager service
        serviceBound = bindService(new Intent(AddComputerManually.this,
                    ComputerManagerService.class), serviceConnection, Service.BIND_AUTO_CREATE);
    }

    private void updateInviteAction(View punch) {
        boolean invitation = com.limelight.computers.HostInvite.parseUri(hostText.getText().toString().trim()) != null;
        punch.setVisibility(invitation ? View.VISIBLE : View.GONE);
    }

    private void startSavedRoute() {
        String id = getIntent().getStringExtra(EXTRA_ROUTE_HOST);
        String key = getIntent().getStringExtra(EXTRA_ROUTE_KEY);
        if (!visible || managerBinder == null || id == null || key == null) return;
        getIntent().removeExtra(EXTRA_ROUTE_KEY);
        for (com.limelight.computers.ConnectionHistory host : managerBinder.connectionHistory()) {
            if (!id.equalsIgnoreCase(host.uuid)) continue;
            for (com.limelight.computers.ConnectionHistory.Route route : host.routes) {
                if (key.equals(route.key)) { connectRoute(host, route); return; }
            }
        }
        hostText.setError(getString(R.string.history_route_gone));
    }

    private void connectRoute(com.limelight.computers.ConnectionHistory host, com.limelight.computers.ConnectionHistory.Route route) {
        submitAddress(route.value, com.limelight.computers.HostInvite.parseUri(route.value) != null, host.uuid);
    }

    private void showHistory() {
        if (managerBinder == null) { hostText.setError(getString(R.string.error_manager_not_running)); return; }
        ConnectionRoutePicker.show(this, managerBinder, null, this::connectRoute);
    }

    // Returns true if the event should be eaten
    private boolean handleDoneEvent() {
        if (!serviceBound) hostText.setError(getString(R.string.error_manager_not_running));
        else submitAddress(hostText.getText().toString());
        return true;
    }
}
