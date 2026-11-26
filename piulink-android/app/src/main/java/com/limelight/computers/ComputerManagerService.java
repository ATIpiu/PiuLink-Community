package com.limelight.computers;

import java.io.IOException;
import java.io.OutputStream;
import java.io.StringReader;
import java.net.InetAddress;
import java.net.UnknownHostException;
import java.util.HashSet;
import java.util.LinkedList;
import java.util.List;
import java.util.LinkedHashSet;
import java.util.ArrayList;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.locks.Lock;
import java.util.concurrent.locks.ReentrantLock;

import com.limelight.LimeLog;
import com.limelight.binding.PlatformBinding;
import com.limelight.discovery.DiscoveryService;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.http.PairingManager;
import com.limelight.nvstream.mdns.MdnsComputer;
import com.limelight.nvstream.mdns.MdnsDiscoveryListener;
import com.limelight.utils.CacheHelper;
import com.limelight.utils.NetHelper;
import com.limelight.utils.ServerHelper;

import android.app.Service;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.Binder;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;

import org.xmlpull.v1.XmlPullParserException;

public class ComputerManagerService extends Service {
    private final java.util.concurrent.ExecutorService appStatusWorker =
            java.util.concurrent.Executors.newSingleThreadExecutor();
    private final java.util.concurrent.ExecutorService desktopCatalogWorker =
            java.util.concurrent.Executors.newFixedThreadPool(2);
    private DesktopLookup warmedDesktop;

    private synchronized void warmDesktop(ComputerDetails pc) {
        if (pc.uuid == null || pc.serverCert == null || pc.activeAddress == null ||
                pc.pairState != PairingManager.PairState.PAIRED) return;
        if (warmedDesktop != null && (warmedDesktop.matches(pc) ||
                !warmedDesktop.apps.isDone() || !warmedDesktop.status.isDone())) return;
        warmedDesktop = new DesktopLookup(this, pc, idManager.getUniqueId());
        desktopCatalogWorker.execute(warmedDesktop.apps);
        desktopCatalogWorker.execute(warmedDesktop.status);
    }

    private synchronized DesktopLookup takeDesktop(ComputerDetails pc) {
        if (warmedDesktop == null || !warmedDesktop.matches(pc)) return null;
        DesktopLookup result = warmedDesktop;
        warmedDesktop = null;
        return result;
    }

    private synchronized void clearWarmedDesktop() {
        warmedDesktop = null;
    }
    private static final int SERVERINFO_POLLING_PERIOD_MS = 1500;
    private static final int APPLIST_POLLING_PERIOD_MS = 30000;
    private static final int APPLIST_FAILED_POLLING_RETRY_MS = 2000;
    private static final int MDNS_QUERY_PERIOD_MS = 1000;
    private static final int MDNS_INITIAL_SETTLE_MS = 600;
    private static final int MDNS_MAX_POLL_ATTEMPTS = 4;
    private static final int OFFLINE_POLL_TRIES = 3;
    private static final int INITIAL_POLL_TRIES = 2;
    private static final int EMPTY_LIST_THRESHOLD = 3;
    private static final int POLL_DATA_TTL_MS = 30000;
    // Give a real Wi-Fi/Ethernet route a bounded chance to answer before an
    // overlay route wins. All candidates are probed concurrently.
    private static final int DIRECT_LAN_PREFERENCE_WINDOW_MS = 750;
    private static final int ADDRESS_POLL_WAIT_INTERVAL_MS = 20;

    private final ComputerManagerBinder binder = new ComputerManagerBinder();

    private ComputerDatabaseManager dbManager;
    private final AtomicInteger dbRefCount = new AtomicInteger(0);

    private IdentityManager idManager;
    private final LinkedList<PollingTuple> pollingTuples = new LinkedList<>();
    private final HashSet<String> pendingHostUuids = new HashSet<>();
    private final java.util.concurrent.ConcurrentMap<String, ComputerDetails.AddressTuple> selectedRoutes = new java.util.concurrent.ConcurrentHashMap<>();
    private final java.util.concurrent.ConcurrentMap<String, Long> inviteRetries = new java.util.concurrent.ConcurrentHashMap<>();
    private final HashSet<String> pendingDiscoveryAddresses = new HashSet<>();
    private volatile ComputerManagerListener listener = null;
    private final AtomicInteger activePolls = new AtomicInteger(0);
    private volatile boolean pollingActive = false;
    private final Lock defaultNetworkLock = new ReentrantLock();
    private final AddressProbeBackoff addressProbeBackoff =
            new AddressProbeBackoff(SystemClock::elapsedRealtime);

    private ConnectivityManager.NetworkCallback networkCallback;

    private DiscoveryService.DiscoveryBinder discoveryBinder;
    private final ServiceConnection discoveryServiceConnection = new ServiceConnection() {
        public void onServiceConnected(ComponentName className, IBinder binder) {
            synchronized (discoveryServiceConnection) {
                DiscoveryService.DiscoveryBinder privateBinder = ((DiscoveryService.DiscoveryBinder)binder);

                // Set us as the event listener
                privateBinder.setListener(createDiscoveryListener());

                // Signal a possible waiter that we're all setup
                discoveryBinder = privateBinder;
                discoveryServiceConnection.notifyAll();
            }
        }

        public void onServiceDisconnected(ComponentName className) {
            discoveryBinder = null;
        }
    };

    // Returns true if the details object was modified
    private boolean runPoll(ComputerDetails details, boolean newPc, int offlineCount) throws InterruptedException {
        return runPoll(details, newPc, offlineCount, null);
    }

    private boolean runPoll(ComputerDetails details, boolean newPc, int offlineCount,
                            ComputerDetails.AddressTuple exact) throws InterruptedException {
        if (!getLocalDatabaseReference()) return false;
        final int pollTriesBeforeOffline = details.state == ComputerDetails.State.UNKNOWN ?
                INITIAL_POLL_TRIES : OFFLINE_POLL_TRIES;

        activePolls.incrementAndGet();

        // Poll the machine
        try {
            ComputerDetails found = exact == null ? null : pollAddresses(details, java.util.Collections.singletonList(exact));
            if (found != null) details.update(found);
            if (exact == null ? !pollComputer(details) : found == null) {
                if (!newPc && offlineCount < pollTriesBeforeOffline) {
                    // Return without calling the listener
                    releaseLocalDatabaseReference();
                    return false;
                }

                details.state = ComputerDetails.State.OFFLINE;
            }
        } catch (InterruptedException e) {
            releaseLocalDatabaseReference();
            throw e;
        } finally {
            activePolls.decrementAndGet();
        }

        // If it's online, update our persistent state
        if (details.state == ComputerDetails.State.ONLINE) {
            ComputerDetails existingComputer = dbManager.getComputerByUUID(details.uuid);

            // Check if it's in the database because it could have been
            // removed after this was issued
            if (!newPc && existingComputer == null) {
                // It's gone
                releaseLocalDatabaseReference();
                return false;
            }

            // If we already have an entry for this computer in the DB, we must
            // combine the existing data with this new data (which may be partially available
            // due to detecting the PC via mDNS) without the saved external address. If we
            // write to the DB without doing this first, we can overwrite our existing data.
            if (existingComputer != null) {
                if (newPc) rememberRoutes(existingComputer);
                existingComputer.update(details);
                dbManager.updateComputer(existingComputer);
            }
            else {
                try {
                    // If the active address is a site-local address (RFC 1918),
                    // then use STUN to populate the external address field if
                    // it's not set already.
                    if (details.remoteAddress == null) {
                        InetAddress addr = InetAddress.getByName(details.activeAddress.address);
                        if (addr.isSiteLocalAddress()) {
                            populateExternalAddress(details);
                        }
                    }
                } catch (UnknownHostException ignored) {}

                dbManager.updateComputer(details);
            }
        }

        // Don't call the listener if this is a failed lookup of a new PC
        ComputerManagerListener activeListener = listener;
        if ((!newPc || details.state == ComputerDetails.State.ONLINE) && activeListener != null) {
            activeListener.notifyComputerUpdated(details);
        }

        releaseLocalDatabaseReference();
        return true;
    }

    private Thread createPollingThread(final PollingTuple tuple) {
        Thread t = new Thread() {
            @Override
            public void run() {

                int offlineCount = 0;
                while (!isInterrupted() && pollingActive && tuple.thread == this) {
                    try {
                        // Only allow one request to the machine at a time
                        synchronized (tuple.networkLock) {
                            // Check if this poll has modified the details
                            if (!runPoll(tuple.computer, false, offlineCount)) {
                                LimeLog.warning(tuple.computer.name + " is offline (try " + offlineCount + ")");
                                offlineCount++;
                            } else {
                                tuple.lastSuccessfulPollMs = SystemClock.elapsedRealtime();
                                offlineCount = 0;
                            }
                        }

                        // Wait until the next polling interval
                        Thread.sleep(SERVERINFO_POLLING_PERIOD_MS);
                    } catch (InterruptedException e) {
                        break;
                    }
                }
            }
        };
        t.setName("Polling thread for " + tuple.computer.name);
        return t;
    }

    public class ComputerManagerBinder extends Binder {
        public void startPolling(ComputerManagerListener listener) {
            // Polling is active
            pollingActive = true;

            // Set the listener
            ComputerManagerService.this.listener = listener;

            // Start mDNS autodiscovery too
            DiscoveryService.DiscoveryBinder discovery = discoveryBinder;
            if (discovery != null) discovery.startDiscovery(MDNS_QUERY_PERIOD_MS);

            synchronized (pollingTuples) {
                for (PollingTuple tuple : pollingTuples) {
                    // Enforce the poll data TTL
                    if (SystemClock.elapsedRealtime() - tuple.lastSuccessfulPollMs > POLL_DATA_TTL_MS) {
                        LimeLog.info("Timing out polled state for "+tuple.computer.name);
                        tuple.computer.state = ComputerDetails.State.UNKNOWN;
                    }

                    // Report this computer initially
                    listener.notifyComputerUpdated(tuple.computer);

                    // This polling thread might already be there
                    if (tuple.thread == null) {
                        tuple.thread = createPollingThread(tuple);
                        tuple.thread.start();
                    }
                }
            }
        }

        public void waitForReady() {
            synchronized (discoveryServiceConnection) {
                try {
                    while (discoveryBinder == null) {
                        // Wait for the bind notification
                        discoveryServiceConnection.wait(1000);
                    }
                } catch (InterruptedException e) {
                    e.printStackTrace();

                    // InterruptedException clears the thread's interrupt status. Since we can't
                    // handle that here, we will re-interrupt the thread to set the interrupt
                    // status back to true.
                    Thread.currentThread().interrupt();
                }
            }
        }

        public void waitForPollingStopped() {
            while (activePolls.get() != 0) {
                try {
                    Thread.sleep(250);
                } catch (InterruptedException e) {
                    e.printStackTrace();

                    // InterruptedException clears the thread's interrupt status. Since we can't
                    // handle that here, we will re-interrupt the thread to set the interrupt
                    // status back to true.
                    Thread.currentThread().interrupt();
                }
            }
        }

        public boolean addComputerBlocking(ComputerDetails fakeDetails) throws InterruptedException {
            String typed = ConnectionHistory.address(fakeDetails.manualAddress);
            boolean saved = ComputerManagerService.this.addComputerBlocking(fakeDetails);
            if (saved && typed != null) {
                ConnectionHistory.explicit(ComputerManagerService.this, fakeDetails.uuid, typed);
                ConnectionHistory.used(ComputerManagerService.this, fakeDetails.uuid, typed);
            }
            return saved;
        }

        public boolean addInvite(String link, boolean directOnly, String expectedHostId) throws IOException, InterruptedException {
            HostInvite invite = HostInvite.parse(link);
            if (expectedHostId != null && !expectedHostId.equalsIgnoreCase(invite.uuid)) return false;
            ConnectionHistory.explicit(ComputerManagerService.this, invite.uuid, link);
            ComputerDetails expected = new ComputerDetails();
            expected.uuid = invite.uuid;
            ComputerDetails saved = getComputer(invite.uuid);
            if (saved != null) expected.serverCert = saved.serverCert;
            ComputerDetails found;
            try {
                if (directOnly && saved != null && saved.serverCert != null) {
                    // A paired historical invitation needs only the authenticated endpoint probe.
                    found = verifyInvitation(saved, invite);
                    if (found == null || !keepVerifiedRoute(found)) return false;
                    rememberAcceptedInvitation(found.uuid, link);
                    return true;
                }
                found = resolveInvite(expected, invite, directOnly);
            } catch (IOException failure) {
                // An already-paired LAN computer remains reachable during a directory outage.
                // Explicit offline/revoked responses and newly supplied rooms must still fail.
                String accepted = savedInvitation(invite.uuid);
                if (directOnly || !HostInvite.isTransient(failure) || !link.equals(accepted)) throw failure;
                found = probePairedLan(saved);
                if (found == null) throw failure;
            }
            if (found == null) return false;
            if (saved != null && saved.serverCert != null) {
                ComputerDetails peer = verifyDesktopEndpoint(saved, found.activeAddress);
                if (peer == null || !keepVerifiedRoute(peer)) return false;
            } else if (!finishAddAtAddress(found)) return false;
            rememberAcceptedInvitation(found.uuid, link);
            return true;
        }

        private void rememberAcceptedInvitation(String uuid, String link) {
            synchronized (ConnectionHistory.class) {
                if (ConnectionHistory.used(ComputerManagerService.this, uuid, link)) storeInvitation(uuid, link);
            }
        }

        public ComputerDetails resolveSavedInvitation(String uuid) throws IOException, InterruptedException {
            ComputerDetails saved = getComputer(uuid);
            String link = savedInvitation(uuid);
            if (saved == null || saved.serverCert == null || HostInvite.parseUri(link) == null) return null;
            HostInvite invite = HostInvite.parse(link);
            if (!uuid.equalsIgnoreCase(invite.uuid)) return null;
            return verifyInvitation(new ComputerDetails(saved), invite);
        }

        private boolean finishAddAtAddress(ComputerDetails found) throws InterruptedException {
            // Discovery has already checked the UUID on this exact route. Refresh pairing once
            // on the winning endpoint, without probing every route a second and third time.
            ComputerDetails exact = new ComputerDetails();
            exact.uuid = found.uuid;
            exact.manualAddress = found.activeAddress;
            addressProbeBackoff.recordSuccess(exact.manualAddress.toString());
            boolean saved = ComputerManagerService.this.addComputerBlocking(exact, true);
            if (saved) selectedRoutes.put(found.uuid.toLowerCase(java.util.Locale.ROOT), found.activeAddress);
            return saved;
        }

        // Only the caller that is still opening this computer commits the chosen route.
        public ComputerDetails resolveDesktopRoute(String uuid) throws InterruptedException, IOException {
            ComputerDetails current = getComputer(uuid);
            if (current == null || current.serverCert == null) return null;
            ConnectivityManager connectivity = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
            if (connectivity.getActiveNetworkInfo() == null) return null;

            ComputerDetails saved = new ComputerDetails(current);
            ConnectionHistory history = ConnectionHistory.snapshot(ComputerManagerService.this, uuid, saved.name);
            LinkedHashSet<ComputerDetails.AddressTuple> addresses = new LinkedHashSet<>();
            String accepted = savedInvitation(uuid);
            HostInvite invite = HostInvite.parseUri(accepted) == null ? null : HostInvite.parse(accepted);
            if (invite != null) {
                DirectInviteTunnel.Path active = DirectInviteTunnel.active(ComputerManagerService.this, invite);
                if (active != null) addresses.add(active.address);
            }
            for (ConnectionHistory.Route route : history.routes) {
                if (route.value.startsWith("https://")) continue;
                try {
                    java.net.URI address = new java.net.URI("moonlight://" + route.value);
                    if (address.getHost() != null && address.getPort() > 0)
                        addresses.add(new ComputerDetails.AddressTuple(address.getHost(), address.getPort()));
                } catch (java.net.URISyntaxException ignored) { }
            }

            // A few stale addresses must not make the P2P invitation wait through TCP timeouts.
            List<ComputerDetails.AddressTuple> queue = new ArrayList<>();
            boolean vpn = NetHelper.isActiveNetworkVpn(ComputerManagerService.this);
            for (ComputerDetails.AddressTuple address : addresses) {
                // Rank literal IPs without doing synchronous DNS before other probes can start.
                String host = address.address;
                if (host.indexOf(':') < 0 && !host.matches("[0-9.]+")) continue;
                Network lan = vpn ? NetHelper.getBindableLocalNetworkForAddress(ComputerManagerService.this, host)
                        : NetHelper.getLocalNetworkForAddress(ComputerManagerService.this, host);
                if (lan != null) queue.add(address);
            }
            List<ComputerDetails.AddressTuple> preferredLan = vpn ? new ArrayList<>(queue) : java.util.Collections.emptyList();
            addresses.removeAll(queue);
            queue.addAll(addresses);
            return DesktopRouteSelector.firstVerified(queue, preferredLan, address -> verifyDesktopEndpoint(saved, address),
                    invite == null ? null : () -> verifyInvitation(saved, invite), DIRECT_LAN_PREFERENCE_WINDOW_MS, 40000);
        }

        private ComputerDetails verifyInvitation(ComputerDetails saved, HostInvite invite) throws IOException, InterruptedException {
            DirectInviteTunnel.Path active = DirectInviteTunnel.active(ComputerManagerService.this, invite);
            if (active != null) {
                ComputerDetails peer = verifyDesktopEndpoint(saved, active.address);
                if (peer != null) return peer;
            }
            if (Thread.currentThread().isInterrupted()) throw new InterruptedException();
            DirectInviteTunnel.Path path = DirectInviteTunnel.connect(ComputerManagerService.this, invite, saved.serverCert, active);
            return verifyDesktopEndpoint(saved, path.address);
        }

        private ComputerDetails verifyDesktopEndpoint(ComputerDetails saved, ComputerDetails.AddressTuple address) {
            if (DirectInviteTunnel.isEndpoint(address.address) &&
                    !address.equals(DirectInviteTunnel.preferred(ComputerManagerService.this, saved.uuid))) return null;
            try {
                int httpsPort = address.equals(saved.activeAddress) ? saved.httpsPort : 0;
                NvHTTP http = new NvHTTP(address, httpsPort, idManager.getUniqueId(), saved.serverCert,
                        PlatformBinding.getCryptoProvider(ComputerManagerService.this));
                Network bound = NetHelper.bindToDirectLocalNetworkWithNetwork(ComputerManagerService.this, http, address.address);
                ComputerDetails peer;
                try {
                    peer = http.getPinnedLanComputerDetails();
                } catch (IOException failure) {
                    if (bound == null || !NetHelper.isNetworkBindingDenied(failure)) throw failure;
                    NetHelper.noteDirectNetworkBindingDenied(bound);
                    http = new NvHTTP(address, httpsPort, idManager.getUniqueId(), saved.serverCert,
                            PlatformBinding.getCryptoProvider(ComputerManagerService.this));
                    peer = http.getPinnedLanComputerDetails();
                }
                if (!saved.uuid.equalsIgnoreCase(peer.uuid) || peer.pairState != PairingManager.PairState.PAIRED) return null;
                peer.activeAddress = address;
                peer.serverCert = saved.serverCert;
                return peer;
            } catch (IOException | XmlPullParserException unavailable) {
                return null;
            }
        }

        private boolean keepVerifiedRoute(ComputerDetails peer) {
            synchronized (pollingTuples) {
                ComputerDetails saved = getComputer(peer.uuid);
                if (saved == null || !peer.serverCert.equals(saved.serverCert)) return false;
                selectedRoutes.put(peer.uuid.toLowerCase(java.util.Locale.ROOT), peer.activeAddress);
                saved.update(peer);
            }
            return true;
        }

        public boolean selectDesktopRoute(ComputerDetails peer) {
            if (!keepVerifiedRoute(peer)) return false;
            String value = ConnectionHistory.address(peer.activeAddress);
            if (value == null && DirectInviteTunnel.isEndpoint(peer.activeAddress.address)) value = savedInvitation(peer.uuid);
            ConnectionHistory.used(ComputerManagerService.this, peer.uuid, value);
            return true;
        }

        public boolean addHistoricalAddress(String uuid, ComputerDetails.AddressTuple address) throws InterruptedException {
            ComputerDetails expected = new ComputerDetails();
            expected.uuid = uuid;
            addressProbeBackoff.recordSuccess(address.toString());
            ComputerDetails saved = getComputer(uuid);
            if (saved != null && saved.serverCert != null) {
                ComputerDetails peer = verifyDesktopEndpoint(saved, address);
                return peer != null && selectDesktopRoute(peer);
            }
            ComputerDetails found = pollAddresses(expected, java.util.Collections.singletonList(address));
            return found != null && acceptHistoricalAddress(found, address);
        }

        private boolean acceptHistoricalAddress(ComputerDetails peer, ComputerDetails.AddressTuple address) throws InterruptedException {
            if (!finishAddAtAddress(peer)) return false;
            ConnectionHistory.used(ComputerManagerService.this, peer.uuid, ConnectionHistory.address(address));
            return true;
        }

        public void renameRoute(String uuid, String value, String label) {
            ConnectionHistory.rename(ComputerManagerService.this, uuid, value, label.trim());
        }

        public void removeRoute(String uuid, String value) {
            synchronized (ConnectionHistory.class) {
                if (value.equals(savedInvitation(uuid))) storeInvitation(uuid, null);
                ConnectionHistory.remove(ComputerManagerService.this, uuid, value);
            }
        }

        public List<ConnectionHistory> connectionHistory() {
            java.util.Map<String,String> names = new java.util.LinkedHashMap<>();
            synchronized (pollingTuples) {
                for (PollingTuple tuple : pollingTuples) {
                    ComputerDetails pc = tuple.computer;
                    rememberRoutes(pc);
                    names.put(pc.uuid.toLowerCase(java.util.Locale.ROOT), pc.name);
                }
            }
            List<ConnectionHistory> result = new java.util.ArrayList<>();
            for (String id : ConnectionHistory.hostIds(ComputerManagerService.this)) {
                ConnectionHistory host = ConnectionHistory.snapshot(ComputerManagerService.this, id, names.get(id));
                if (!host.routes.isEmpty()) result.add(host);
            }
            java.util.Collections.sort(result, (a, b) -> a.name.compareToIgnoreCase(b.name));
            return result;
        }

        private ComputerDetails probePairedLan(ComputerDetails saved) {
            if (saved == null || saved.serverCert == null) return null;
            for (ComputerDetails.AddressTuple address : new java.util.LinkedHashSet<>(java.util.Arrays.asList(
                    saved.localAddress, saved.activeAddress, saved.manualAddress))) {
                if (address == null || DirectInviteTunnel.isEndpoint(address.address) ||
                        address.address.startsWith("127.") || !NetHelper.isAddressOnLocalNetwork(ComputerManagerService.this, address.address)) continue;
                ComputerDetails peer = verifyDesktopEndpoint(saved, address);
                if (peer != null) {
                    peer.localAddress = address;
                    LimeLog.info("PiuLink verified paired LAN endpoint during invitation service interruption");
                    return peer;
                }
            }
            return null;
        }

        /**
         * Installs the listener used by ApplistPoller without also starting the
         * periodic serverinfo workers. AppView must not run those requests in
         * parallel with the first authenticated applist request after pairing:
         * an HTTP fallback response cannot authenticate the client and reports
         * PairStatus=0, which would otherwise close the newly opened AppView.
         */
        public void startAppListPolling(ComputerManagerListener listener) {
            ComputerManagerService.this.listener = listener;
        }

        public boolean canAddNewComputer() {
            synchronized (pollingTuples) {
                return hasCapacityForNewHostLocked();
            }
        }

        public int getSavedComputerCount() {
            synchronized (pollingTuples) {
                return pollingTuples.size();
            }
        }

        public void removeComputer(ComputerDetails computer) {
            ComputerManagerService.this.removeComputer(computer);
        }

        public void stopPolling() {
            // Just call the unbind handler to cleanup
            ComputerManagerService.this.onUnbind(null);
        }

        public void warmDesktopEntry(ComputerDetails computer) {
            warmDesktop(computer);
        }

        public ApplistPoller createAppListPoller(ComputerDetails computer, boolean hasUsableCache, boolean openDesktop) {
            return new ApplistPoller(computer, hasUsableCache, openDesktop);
        }

        public String getUniqueId() {
            return idManager.getUniqueId();
        }

        public ComputerDetails getComputer(String uuid) {
            synchronized (pollingTuples) {
                for (PollingTuple tuple : pollingTuples) {
                    if (uuid.equalsIgnoreCase(tuple.computer.uuid)) {
                        return tuple.computer;
                    }
                }
            }

            return null;
        }

        public void invalidateStateForComputer(String uuid) {
            synchronized (pollingTuples) {
                for (PollingTuple tuple : pollingTuples) {
                    if (uuid.equals(tuple.computer.uuid)) {
                        // We need the network lock to prevent a concurrent poll
                        // from wiping this change out
                        synchronized (tuple.networkLock) {
                            tuple.computer.state = ComputerDetails.State.UNKNOWN;
                        }
                    }
                }
            }
        }

        public void persistPairedCertificate(String uuid) {
            synchronized (pollingTuples) {
                for (PollingTuple tuple : pollingTuples) {
                    if (uuid.equals(tuple.computer.uuid)) {
                        // Pairing updates the certificate on the shared ComputerDetails instance.
                        // Persist it immediately instead of waiting for a later serverinfo poll,
                        // which may be interrupted while Sunshine finishes the pairing exchange.
                        if (getLocalDatabaseReference()) {
                            try {
                                dbManager.updateComputer(tuple.computer);
                            } finally {
                                releaseLocalDatabaseReference();
                            }
                        }
                        return;
                    }
                }
            }
        }
    }

    @Override
    public boolean onUnbind(Intent intent) {
        if (discoveryBinder != null) {
            // Stop mDNS autodiscovery
            discoveryBinder.stopDiscovery();
        }

        // Stop polling
        pollingActive = false;
        synchronized (pollingTuples) {
            for (PollingTuple tuple : pollingTuples) {
                if (tuple.thread != null) {
                    // Interrupt and remove the thread
                    tuple.thread.interrupt();
                    tuple.thread = null;
                }
            }
        }

        // Remove the listener
        listener = null;

        return false;
    }

    private void populateExternalAddress(ComputerDetails details) {
        boolean boundToNetwork = false;
        boolean activeNetworkIsVpn = NetHelper.isActiveNetworkVpn(this);
        ConnectivityManager connMgr = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);

        // Check if we're currently connected to a VPN which may send our
        // STUN request from an unexpected interface
        if (activeNetworkIsVpn) {
            // Acquire the default network lock since we could be changing global process state
            defaultNetworkLock.lock();

            // On Lollipop or later, we can bind our process to the underlying interface
            // to ensure our STUN request goes out on that interface or not at all (which is
            // preferable to getting a VPN endpoint address back).
            Network[] networks = connMgr.getAllNetworks();
            for (Network net : networks) {
                NetworkCapabilities netCaps = connMgr.getNetworkCapabilities(net);
                if (netCaps != null) {
                    if (!netCaps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) &&
                            !netCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
                        // This network looks like an underlying multicast-capable transport,
                        // so let's guess that it's probably where our mDNS response came from.
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                            if (connMgr.bindProcessToNetwork(net)) {
                                boundToNetwork = true;
                                break;
                            }
                        } else if (ConnectivityManager.setProcessDefaultNetwork(net)) {
                            boundToNetwork = true;
                            break;
                        }
                    }
                }
            }

            // Perform the STUN request if we're not on a VPN or if we bound to a network
            if (!activeNetworkIsVpn || boundToNetwork) {
                String stunResolvedAddress = NvConnection.findExternalAddressForMdns("stun.moonlight-stream.org", 3478);
                if (stunResolvedAddress != null) {
                    // We don't know for sure what the external port is, so we will have to guess.
                    // When we contact the PC (if we haven't already), it will update the port.
                    details.remoteAddress = new ComputerDetails.AddressTuple(stunResolvedAddress, details.guessExternalPort());
                }
            }

            // Unbind from the network
            if (boundToNetwork) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                    connMgr.bindProcessToNetwork(null);
                } else {
                    ConnectivityManager.setProcessDefaultNetwork(null);
                }
            }

            // Unlock the network state
            if (activeNetworkIsVpn) {
                defaultNetworkLock.unlock();
            }
        }
    }

    private MdnsDiscoveryListener createDiscoveryListener() {
        return new MdnsDiscoveryListener() {
            @Override
            public void notifyComputerAdded(MdnsComputer computer) {
                ComputerDetails details = new ComputerDetails();

                // Populate the computer template with mDNS info
                if (computer.getLocalAddress() != null) {
                    details.localAddress = new ComputerDetails.AddressTuple(computer.getLocalAddress().getHostAddress(), computer.getPort());
                }
                if (computer.getIpv6Address() != null) {
                    details.ipv6Address = new ComputerDetails.AddressTuple(computer.getIpv6Address().getHostAddress(), computer.getPort());
                }

                addDiscoveredComputerWithRetry(details);
            }

            @Override
            public void notifyDiscoveryFailure(Exception e) {
                LimeLog.severe("mDNS discovery failed");
                e.printStackTrace();
            }
        };
    }

    private void addDiscoveredComputerWithRetry(ComputerDetails discoveredDetails) {
        final String discoveryKey = String.valueOf(discoveredDetails.localAddress) + "|" +
                String.valueOf(discoveredDetails.ipv6Address);
        synchronized (pendingDiscoveryAddresses) {
            if (!pendingDiscoveryAddresses.add(discoveryKey)) {
                return;
            }
        }

        Thread retryThread = new Thread(() -> {
            try {
                // NsdManager may resolve a host immediately after Wi-Fi becomes available,
                // before the app's route and any VPN underlay have settled.
                Thread.sleep(MDNS_INITIAL_SETTLE_MS);
                for (int attempt = 1; attempt <= MDNS_MAX_POLL_ATTEMPTS && pollingActive; attempt++) {
                    ComputerDetails attemptDetails = new ComputerDetails(discoveredDetails);
                    if (addComputerBlocking(attemptDetails)) {
                        return;
                    }

                    LimeLog.warning("Auto-discovered PC did not respond (attempt " + attempt + "/" +
                            MDNS_MAX_POLL_ATTEMPTS + "): " + discoveryKey);
                    if (attempt < MDNS_MAX_POLL_ATTEMPTS) {
                        Thread.sleep(500L << attempt);
                    }
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            } finally {
                synchronized (pendingDiscoveryAddresses) {
                    pendingDiscoveryAddresses.remove(discoveryKey);
                }
            }
        });
        retryThread.setName("mDNS host retry - " + discoveryKey);
        retryThread.start();
    }

    private void addTuple(ComputerDetails details) {
        synchronized (pollingTuples) {
            for (PollingTuple tuple : pollingTuples) {
                // Check if this is the same computer
                if (tuple.computer.uuid.equals(details.uuid)) {
                    // Update the saved computer with potentially new details
                    tuple.computer.update(details);

                    // Start a polling thread if polling is active
                    if (pollingActive && tuple.thread == null) {
                        tuple.thread = createPollingThread(tuple);
                        tuple.thread.start();
                    }

                    // Found an entry so we're done
                    return;
                }
            }

            // If we got here, we didn't find an entry
            PollingTuple tuple = new PollingTuple(details, null);
            if (pollingActive) {
                tuple.thread = createPollingThread(tuple);
            }
            pollingTuples.add(tuple);
            if (tuple.thread != null) {
                tuple.thread.start();
            }
        }
    }

    public boolean addComputerBlocking(ComputerDetails fakeDetails) throws InterruptedException {
        return addComputerBlocking(fakeDetails, false);
    }

    private boolean addComputerBlocking(ComputerDetails fakeDetails, boolean alreadyDiscovered) throws InterruptedException {
        boolean reservedNewHost = false;
        try {
            // We cannot use runPoll() here because it will attempt to persist the state of the
            // machine before we have loaded a pinned certificate.
            if (alreadyDiscovered || pollComputer(fakeDetails)) {
                // See if we have record of this PC to pull its pinned cert. New-host capacity is
                // reserved until addTuple() completes, closing the race between concurrent mDNS
                // and manual additions on the one-host Free tier.
                synchronized (pollingTuples) {
                    boolean existingHost = false;
                    for (PollingTuple tuple : pollingTuples) {
                        if (tuple.computer.uuid.equalsIgnoreCase(fakeDetails.uuid)) {
                            fakeDetails.serverCert = tuple.computer.serverCert;
                            existingHost = true;
                            break;
                        }
                    }

                    if (!existingHost) {
                        if (!hasCapacityForNewHostLocked()) {
                            LimeLog.info("Free tier host limit reached; ignoring new PC " + fakeDetails.uuid);
                            return false;
                        }
                        pendingHostUuids.add(fakeDetails.uuid);
                        reservedNewHost = true;
                    }
                }

                // Poll again, possibly with the pinned cert, to get accurate pairing information.
                // This will insert the host into the database too.
                if (!runPoll(fakeDetails, true, 0, alreadyDiscovered ? fakeDetails.manualAddress : null)) return false;
            }

            // If the machine is reachable, it was successful
            if (fakeDetails.state == ComputerDetails.State.ONLINE) {
                LimeLog.info("New PC ("+fakeDetails.name+") is UUID "+fakeDetails.uuid);

                // Add before releasing the capacity reservation so a concurrent discovery cannot
                // pass the Free-tier capacity check in between persistence and tuple creation.
                addTuple(fakeDetails);
                rememberRoutes(fakeDetails);
                return true;
            }
            return false;
        } finally {
            if (reservedNewHost) {
                synchronized (pollingTuples) {
                    pendingHostUuids.remove(fakeDetails.uuid);
                }
            }
        }
    }

    private void rememberRoutes(ComputerDetails pc) {
        ConnectionHistory.remember(this, pc.uuid, ConnectionHistory.address(pc.manualAddress),
                ConnectionHistory.address(pc.localAddress), ConnectionHistory.address(pc.remoteAddress),
                ConnectionHistory.address(pc.ipv6Address), savedInvitation(pc.uuid));
    }

    private boolean hasCapacityForNewHostLocked() {
        // Saved hosts are a local feature and are never gated by an account or purchase.
        return true;
    }

    public void removeComputer(ComputerDetails computer) {
        if (!getLocalDatabaseReference()) {
            return;
        }

        // Remove it from the database
        dbManager.deleteComputer(computer);
        storeInvitation(computer.uuid, null);
        ConnectionHistory.forget(this, computer.uuid);
        inviteRetries.remove(computer.uuid);
        selectedRoutes.remove(computer.uuid.toLowerCase(java.util.Locale.ROOT));
        DirectInviteTunnel.close(computer.uuid);
        clearWarmedDesktop();

        synchronized (pollingTuples) {
            // Remove the computer from the computer list
            for (PollingTuple tuple : pollingTuples) {
                if (tuple.computer.uuid.equals(computer.uuid)) {
                    if (tuple.thread != null) {
                        // Interrupt the thread on this entry
                        tuple.thread.interrupt();
                        tuple.thread = null;
                    }
                    pollingTuples.remove(tuple);
                    break;
                }
            }
        }

        releaseLocalDatabaseReference();
    }

    private boolean getLocalDatabaseReference() {
        if (dbRefCount.get() == 0) {
            return false;
        }

        dbRefCount.incrementAndGet();
        return true;
    }

    private void releaseLocalDatabaseReference() {
        if (dbRefCount.decrementAndGet() == 0) {
            dbManager.close();
        }
    }

    private static boolean isUnreachableFailure(Throwable error) {
        for (Throwable cause = error; cause != null; cause = cause.getCause()) {
            if (cause instanceof java.net.SocketTimeoutException ||
                    cause instanceof java.net.ConnectException ||
                    cause instanceof java.net.NoRouteToHostException ||
                    cause instanceof UnknownHostException) {
                return true;
            }
        }
        return false;
    }

    private ComputerDetails tryPollIp(ComputerDetails details, ComputerDetails.AddressTuple address, boolean[] unreachable) {
        try {
            // If the current address's port number matches the active address's port number, we can also assume
            // the HTTPS port will also match. This assumption is currently safe because Sunshine sets all ports
            // as offsets from the base HTTP port and doesn't allow custom HttpsPort responses for WAN vs LAN.
            boolean portMatchesActiveAddress = details.state == ComputerDetails.State.ONLINE &&
                    details.activeAddress != null && address.port == details.activeAddress.port;

            // Public serverinfo is available over HTTP before pairing. Avoid using a
            // stale pinned certificate while the saved state is unpaired, otherwise
            // a reinstalled Host adds a full HTTPS read timeout to every refresh.
            NvHTTP http = new NvHTTP(address, portMatchesActiveAddress ? details.httpsPort : 0,
                    idManager.getUniqueId(),
                    details.pairState == PairingManager.PairState.PAIRED ? details.serverCert : null,
                    PlatformBinding.getCryptoProvider(ComputerManagerService.this));
            Network boundToLocalNetwork = NetHelper.bindToDirectLocalNetworkWithNetwork(
                    ComputerManagerService.this, http, address.address);

            // If this PC is currently online at this address, extend the timeouts to allow more time for the PC to respond.
            boolean isLikelyOnline = details.state == ComputerDetails.State.ONLINE && address.equals(details.activeAddress);

            // Polling only needs public discovery metadata. Avoid an authenticated
            // HTTPS probe on every refresh: on lossy cellular/Tailnet routes those
            // probes can pile up and compete with the actual launch handshake.
            ComputerDetails newDetails;
            try {
                newDetails = http.getComputerDetails(http.getPublicServerInfo(isLikelyOnline));
            } catch (IOException bindingError) {
                if (boundToLocalNetwork == null || !NetHelper.isNetworkBindingDenied(bindingError)) {
                    throw bindingError;
                }

                NetHelper.noteDirectNetworkBindingDenied(boundToLocalNetwork);
                // A few Android VPN/vendor combinations enumerate the Wi-Fi underlay
                // but prohibit applications from binding sockets to it.  Retry through
                // the system-selected route, which still excludes non-matching traffic
                // from a split-tunnel VPN such as Tailscale.
                LimeLog.warning("Direct network binding was denied for " + address +
                        "; retrying through the Android default route");
                http = new NvHTTP(address, portMatchesActiveAddress ? details.httpsPort : 0,
                        idManager.getUniqueId(),
                        details.pairState == PairingManager.PairState.PAIRED ? details.serverCert : null,
                        PlatformBinding.getCryptoProvider(ComputerManagerService.this));
                newDetails = http.getComputerDetails(http.getPublicServerInfo(isLikelyOnline));
            }
            newDetails.pairState = PairingStatePersistencePolicy.resolvePublicPollState(
                    details.pairState, details.serverCert != null, newDetails.pairState);
            if (details.serverCert != null && newDetails.pairState == PairingManager.PairState.PAIRED) {
                newDetails.serverCert = details.serverCert;
            }

            // Sunshine may report a Tailscale adapter as LocalIP even when this
            // request arrived through an mDNS-discovered Wi-Fi address. Preserve
            // the address whose direct Android route actually succeeded so later
            // refreshes don't replace the LAN endpoint with the Tailnet endpoint.
            if (NetHelper.isAddressOnLocalNetwork(ComputerManagerService.this, address.address)) {
                newDetails.localAddress = address;
            }

            // Check if this is the PC we expected
            if (newDetails.uuid == null) {
                LimeLog.severe("Polling returned no UUID!");
                return null;
            }
            // details.uuid can be null on initial PC add
            else if (details.uuid != null && !details.uuid.equalsIgnoreCase(newDetails.uuid)) {
                // We got the wrong PC!
                LimeLog.info("Polling returned the wrong PC!");
                return null;
            }

            return newDetails;
        } catch (XmlPullParserException e) {
            e.printStackTrace();
            return null;
        } catch (IOException e) {
            if (unreachable != null && unreachable.length > 0 && isUnreachableFailure(e)) {
                unreachable[0] = true;
            }
            LimeLog.warning("Host poll failed at " + address + ": " +
                    e.getClass().getSimpleName() + ": " + e.getMessage());
            return null;
        }
    }

    private static class ParallelPollTuple {
        public ComputerDetails.AddressTuple address;
        public ComputerDetails existingDetails;

        public volatile boolean complete;
        public volatile long completedAtMs;
        public Thread pollingThread;
        public volatile ComputerDetails returnedDetails;

        public ParallelPollTuple(ComputerDetails.AddressTuple address, ComputerDetails existingDetails) {
            this.address = address;
            this.existingDetails = existingDetails;
        }

        public void interrupt() {
            if (pollingThread != null) {
                pollingThread.interrupt();
            }
        }
    }

    private void startParallelPollThread(ParallelPollTuple tuple, HashSet<ComputerDetails.AddressTuple> uniqueAddresses) {
        // Don't bother starting a polling thread for an address that doesn't exist
        // or if the address has already been polled with an earlier tuple
        if (tuple.address == null || !uniqueAddresses.add(tuple.address)) {
            tuple.complete = true;
            tuple.returnedDetails = null;
            return;
        }

        String addressKey = tuple.address.toString();
        boolean activeTunnel = DirectInviteTunnel.isEndpoint(tuple.address.address);
        if (!addressProbeBackoff.shouldProbe(addressKey, activeTunnel)) {
            tuple.complete = true;
            tuple.returnedDetails = null;
            LimeLog.info("Skipping backed-off Host address " + tuple.address + " for " +
                    addressProbeBackoff.remainingDelayMs(addressKey) + " ms");
            return;
        }

        tuple.pollingThread = new Thread() {
            @Override
            public void run() {
                boolean[] unreachable = new boolean[1];
                ComputerDetails details = tryPollIp(tuple.existingDetails, tuple.address, unreachable);

                // Winning probes interrupt the other address threads. Pure cancellation is not
                // evidence that an address is unreachable, but an explicit connect/timeout failure is.
                if (details != null) {
                    addressProbeBackoff.recordSuccess(addressKey);
                }
                else if (!activeTunnel && (unreachable[0] || !isInterrupted())) {
                    addressProbeBackoff.recordFailure(addressKey);
                }

                synchronized (tuple) {
                    tuple.complete = true; // Done
                    tuple.returnedDetails = details; // Polling result
                    tuple.completedAtMs = SystemClock.elapsedRealtime();

                    tuple.notify();
                }
            }
        };
        tuple.pollingThread.setName("Parallel Poll - "+tuple.address+" - "+tuple.existingDetails.name);
        tuple.pollingThread.start();
    }

    private ComputerDetails parallelPollPc(ComputerDetails details) throws InterruptedException {
        String id = details.uuid == null ? null : details.uuid.toLowerCase(java.util.Locale.ROOT);
        ComputerDetails.AddressTuple selected = id == null ? null : selectedRoutes.get(id);
        if (selected == null || selected.address.startsWith("127.")) selected = DirectInviteTunnel.preferred(this, details.uuid);
        if (selected != null) {
            ComputerDetails connected = pollAddresses(details, java.util.Collections.singletonList(selected));
            if (connected != null) return connected;
            if (id != null) selectedRoutes.remove(id, selected);
        }
        return pollAddresses(details, java.util.Arrays.asList(details.localAddress,
                details.manualAddress, details.remoteAddress, details.ipv6Address));
    }

    private ComputerDetails resolveInvite(ComputerDetails details, HostInvite invite, boolean directOnly) throws IOException, InterruptedException {
        DirectInviteTunnel.Path active = DirectInviteTunnel.active(ComputerManagerService.this, invite);
        if (active != null) {
            ComputerDetails reachable = pollAddresses(details, java.util.Collections.singletonList(active.address));
            if (reachable != null) return reachable;
        }
        List<ComputerDetails.AddressTuple> published = java.util.Collections.emptyList();
        if (!directOnly) {
            try {
                published = invite.resolve();
                ComputerDetails reachable = pollAddresses(details, published, SystemClock.elapsedRealtime() + DIRECT_LAN_PREFERENCE_WINDOW_MS);
                if (reachable != null) return reachable;
            } catch (IOException failure) {
                if (!HostInvite.isTransient(failure)) throw failure;
                // The address directory and the authenticated punch exchange are separate requests.
                LimeLog.info("PiuLink invitation address lookup interrupted (" + failure.getClass().getSimpleName() +
                        "); trying P2P signaling");
            }
        }
        try {
            DirectInviteTunnel.Path tunnel = DirectInviteTunnel.connect(this, invite, details.serverCert, active);
            ComputerDetails reachable = pollAddresses(details, java.util.Collections.singletonList(tunnel.address));
            if (reachable != null) {
                LimeLog.info("PiuLink P2P Host UUID verified through tunnel");
                return reachable;
            }
            LimeLog.warning("PiuLink P2P tunnel opened but Host discovery did not verify the expected UUID");
        } catch (IOException failure) {
            if (published.isEmpty() || failure instanceof java.io.FileNotFoundException) throw failure;
            // A UDP negotiation failure says nothing about a slower TCP route. Recheck the
            // invitation before falling back after a definitive (non-network) signaling error.
            if (!HostInvite.isTransient(failure)) published = invite.resolve();
            ComputerDetails slowRoute = pollAddresses(details, published);
            if (slowRoute != null) return slowRoute;
            throw failure;
        }
        return published.isEmpty() ? null : pollAddresses(details, published);
    }

    private ComputerDetails pollAddresses(ComputerDetails details, List<ComputerDetails.AddressTuple> addresses) throws InterruptedException {
        return pollAddresses(details, addresses, Long.MAX_VALUE);
    }

    private ComputerDetails pollAddresses(ComputerDetails details, List<ComputerDetails.AddressTuple> addresses,
                                         long deadline) throws InterruptedException {
        ParallelPollTuple[] candidates = new ParallelPollTuple[addresses.size()];
        HashSet<ComputerDetails.AddressTuple> uniqueAddresses = new HashSet<>();
        for (int i = 0; i < candidates.length; i++) {
            candidates[i] = new ParallelPollTuple(addresses.get(i), details);
            startParallelPollThread(candidates[i], uniqueAddresses);
        }

        try {

            // Do not trust Sunshine's field name here: Windows can report the
            // Tailscale TUN adapter as LocalIP. Classify every saved endpoint
            // from Android's actual Wi-Fi/Ethernet routes instead. This lets one
            // host retain both a LAN address and a Tailnet/MagicDNS address.
            ParallelPollTuple directLan = findDirectLanCandidate(candidates);
            long directLanDeadline = directLan == null ? 0 :
                    SystemClock.elapsedRealtime() + DIRECT_LAN_PREFERENCE_WINDOW_MS;

            while (true) {
                if (directLan != null && directLan.returnedDetails != null) {
                    return selectPollResult(directLan);
                }

                ParallelPollTuple quickestSuccess = findQuickestSuccessfulCandidate(candidates);
                boolean allComplete = areAllCandidatesComplete(candidates);
                if (quickestSuccess != null &&
                        (directLan == null || directLan.complete ||
                                SystemClock.elapsedRealtime() >= directLanDeadline)) {
                    return selectPollResult(quickestSuccess);
                }
                if (allComplete || SystemClock.elapsedRealtime() >= deadline) {
                    return quickestSuccess == null ? null : selectPollResult(quickestSuccess);
                }

                Thread.sleep(ADDRESS_POLL_WAIT_INTERVAL_MS);
            }
        } finally {
            // Stop any further polling if we've found a working address or we've been
            // interrupted by an attempt to stop polling.
            for (ParallelPollTuple candidate : candidates) candidate.interrupt();
        }

    }

    private ParallelPollTuple findDirectLanCandidate(ParallelPollTuple[] candidates) {
        for (ParallelPollTuple candidate : candidates) {
            if (candidate.address != null &&
                    NetHelper.isAddressOnLocalNetwork(this, candidate.address.address)) {
                return candidate;
            }
        }
        return null;
    }

    private static boolean areAllCandidatesComplete(ParallelPollTuple[] candidates) {
        for (ParallelPollTuple candidate : candidates) {
            if (!candidate.complete) {
                return false;
            }
        }
        return true;
    }

    private static ParallelPollTuple findQuickestSuccessfulCandidate(ParallelPollTuple[] candidates) {
        ParallelPollTuple quickest = null;
        for (ParallelPollTuple candidate : candidates) {
            if (candidate.returnedDetails != null &&
                    (quickest == null || candidate.completedAtMs < quickest.completedAtMs)) {
                quickest = candidate;
            }
        }
        return quickest;
    }

    private static ComputerDetails selectPollResult(ParallelPollTuple result) {
        result.returnedDetails.activeAddress = result.address;
        return result.returnedDetails;
    }

    private String savedInvitation(String uuid) {
        return ConnectionHistory.lastAcceptedInvite(this, uuid);
    }

    private void storeInvitation(String uuid, String link) {
        android.content.SharedPreferences preferences = getSharedPreferences("host-invitations", MODE_PRIVATE);
        android.content.SharedPreferences.Editor edit = preferences.edit();
        String normalized = uuid.toLowerCase(java.util.Locale.ROOT);
        for (String key : preferences.getAll().keySet()) {
            if (!key.equals(normalized) && key.equalsIgnoreCase(normalized)) edit.remove(key);
        }
        edit.putString(normalized, link).apply();
    }

    private boolean pollComputer(ComputerDetails details) throws InterruptedException {
        // Poll all addresses in parallel to speed up the process
        LimeLog.info("Starting parallel poll for "+details.name+" ("+details.localAddress +", "+details.remoteAddress +", "+details.manualAddress+", "+details.ipv6Address+")");
        ComputerDetails polledDetails = parallelPollPc(details);
        if (polledDetails == null && details.uuid != null) {
            String link = savedInvitation(details.uuid);
            long now = SystemClock.elapsedRealtime();
            if (link != null && now >= inviteRetries.getOrDefault(details.uuid, 0L)) {
                inviteRetries.put(details.uuid, now + 30000);
                try {
                    HostInvite invite = HostInvite.parse(link);
                    if (invite.uuid.equalsIgnoreCase(details.uuid)) {
                        polledDetails = resolveInvite(details, invite, false);
                        if (polledDetails != null) polledDetails.manualAddress = polledDetails.activeAddress;
                    }
                } catch (IOException | IllegalArgumentException unavailable) {
                    LimeLog.info("Host invitation lookup unavailable (" + unavailable.getClass().getSimpleName() + ")");
                }
            }
        }
        LimeLog.info("Parallel poll for "+details.name+" returned address: "+
                (polledDetails == null ? null : polledDetails.activeAddress));

        if (polledDetails != null) {
            details.update(polledDetails);
            return true;
        }
        else {
            return false;
        }
    }

    @Override
    public void onCreate() {
        // Bind to the discovery service
        bindService(new Intent(this, DiscoveryService.class),
                discoveryServiceConnection, Service.BIND_AUTO_CREATE);

        // Lookup or generate this device's UID
        idManager = new IdentityManager(this);

        // Initialize the DB
        dbManager = new ComputerDatabaseManager(this);
        dbRefCount.set(1);

        // Grab known machines into our computer list
        if (!getLocalDatabaseReference()) {
            return;
        }

        for (ComputerDetails computer : dbManager.getAllComputers()) {
            rememberRoutes(computer);
            addTuple(computer);
        }

        releaseLocalDatabaseReference();

        // Monitor for network changes to invalidate our PC state
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            networkCallback = new ConnectivityManager.NetworkCallback() {
                @Override
                public void onAvailable(Network network) {
                    LimeLog.info("Resetting PC state for new available network");
                    NetHelper.resetDirectNetworkBindingState();
                    selectedRoutes.clear();
                    inviteRetries.clear();
                    addressProbeBackoff.resetForNetworkChange();
                    clearWarmedDesktop();
                    if (!pollingActive) {
                        return;
                    }
                    ComputerManagerListener activeListener = listener;
                    synchronized (pollingTuples) {
                        for (PollingTuple tuple : pollingTuples) {
                            tuple.computer.state = ComputerDetails.State.UNKNOWN;
                            if (activeListener != null) {
                                activeListener.notifyComputerUpdated(tuple.computer);
                            }
                        }
                    }
                }

                @Override
                public void onLost(Network network) {
                    LimeLog.info("Offlining PCs due to network loss");
                    NetHelper.resetDirectNetworkBindingState();
                    selectedRoutes.clear();
                    inviteRetries.clear();
                    addressProbeBackoff.resetForNetworkChange();
                    clearWarmedDesktop();
                    ComputerManagerListener activeListener = listener;
                    synchronized (pollingTuples) {
                        for (PollingTuple tuple : pollingTuples) {
                            tuple.computer.state = ComputerDetails.State.OFFLINE;
                            if (activeListener != null) {
                                activeListener.notifyComputerUpdated(tuple.computer);
                            }
                        }
                    }
                }
            };

            ConnectivityManager connMgr = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
            connMgr.registerDefaultNetworkCallback(networkCallback);
        }
    }

    @Override
    public void onDestroy() {
        appStatusWorker.shutdownNow();
        desktopCatalogWorker.shutdownNow();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            ConnectivityManager connMgr = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
            connMgr.unregisterNetworkCallback(networkCallback);
        }

        if (discoveryBinder != null) {
            // Unbind from the discovery service
            unbindService(discoveryServiceConnection);
        }

        // FIXME: Should await termination here but we have timeout issues in HttpURLConnection

        // Remove the initial DB reference
        releaseLocalDatabaseReference();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return binder;
    }

    public class ApplistPoller {
        private volatile Thread thread;
        private final ComputerDetails computer;
        private final Object pollEvent = new Object();
        private volatile boolean receivedAppList;
        private final java.util.concurrent.atomic.AtomicBoolean refreshStatus = new java.util.concurrent.atomic.AtomicBoolean();

        private boolean openDesktop;

        public ApplistPoller(ComputerDetails computer, boolean hasUsableCache, boolean openDesktop) {
            this.computer = computer;
            receivedAppList = hasUsableCache;
            this.openDesktop = openDesktop;
        }

        public void pollNow() {
            synchronized (pollEvent) {
                refreshStatus.set(true);
                pollEvent.notify();
            }
        }

        private boolean waitPollingDelay() {
            try {
                synchronized (pollEvent) {
                    if (Thread.currentThread() != thread) return false;
                    if (refreshStatus.get() && computer.state == ComputerDetails.State.ONLINE && computer.uuid != null &&
                            computer.pairState == PairingManager.PairState.PAIRED) return true;
                    if (receivedAppList) {
                        // If we've already reported an app list successfully,
                        // wait the full polling period
                        pollEvent.wait(APPLIST_POLLING_PERIOD_MS);
                    }
                    else {
                        // If we've failed to get an app list so far, retry much earlier
                        pollEvent.wait(APPLIST_FAILED_POLLING_RETRY_MS);
                    }
                }
            } catch (InterruptedException e) {
                return false;
            }

            return thread == Thread.currentThread() && !Thread.currentThread().isInterrupted();
        }

        private PollingTuple getPollingTuple(ComputerDetails details) {
            synchronized (pollingTuples) {
                for (PollingTuple tuple : pollingTuples) {
                    if (details.uuid.equals(tuple.computer.uuid)) {
                        return tuple;
                    }
                }
            }

            return null;
        }

        public synchronized void start() {
            if (thread != null && thread.isAlive()) return;
            refreshStatus.set(true);
            thread = new Thread() {
                @Override
                public void run() {
                    int emptyAppListResponses = 0;
                    do {
                        // Can't poll if it's not online or paired
                        if (computer.state != ComputerDetails.State.ONLINE ||
                                computer.pairState != PairingManager.PairState.PAIRED) {
                            ComputerManagerListener activeListener = listener;
                            if (activeListener != null) {
                                activeListener.notifyComputerUpdated(computer);
                            }
                            continue;
                        }

                        // Can't poll if there's no UUID yet
                        if (computer.uuid == null) {
                            continue;
                        }

                        ConnectivityManager connectivityManager =
                                (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
                        boolean checkRunning = refreshStatus.getAndSet(false);
                        boolean useCache = receivedAppList && connectivityManager != null &&
                                connectivityManager.isActiveNetworkMetered();
                        if (useCache && !checkRunning) {
                            // The cached list is sufficient while browsing. Avoid periodic
                            // authenticated refreshes competing with a stream launch on cellular.
                            continue;
                        }

                        java.util.concurrent.Future<Integer> runningApp = null;
                        Integer verifiedRunningApp = null;
                        try {
                            ServerHelper.getCurrentAddressFromComputer(computer);
                            DesktopLookup lookup = openDesktop ? takeDesktop(computer) : null;
                            if (openDesktop) {
                                LimeLog.info("Desktop entry lookup " + (lookup == null ? "cold" :
                                        lookup.apps.isDone() && lookup.status.isDone() ? "ready" : "in flight"));
                                openDesktop = false;
                            }
                            boolean prefetched = lookup != null;
                            if (!prefetched) lookup = new DesktopLookup(ComputerManagerService.this, computer, idManager.getUniqueId());
                            if (checkRunning && computer.serverCert != null) {
                                runningApp = lookup.status;
                                if (!prefetched) appStatusWorker.execute(lookup.status);
                            }
                            String appList = null;
                            if (!useCache) {
                                lookup.apps.run(); // Joins a prefetch when one is already reading.
                                try { appList = lookup.apps.get(); }
                                catch (InterruptedException interrupted) {
                                    Thread.currentThread().interrupt();
                                    return;
                                } catch (java.util.concurrent.ExecutionException failure) {
                                    throw new IOException("Cannot load published applications", failure.getCause());
                                }
                            }

                            if (Thread.currentThread() != thread) return;
                            if (runningApp != null) {
                                try {
                                    int current = runningApp.get();
                                    if (Thread.currentThread() != thread) return;
                                    verifiedRunningApp = current;
                                    if (computer.runningGameId != current) {
                                        computer.runningGameId = current;
                                        ComputerManagerListener activeListener = listener;
                                        if (activeListener != null) activeListener.notifyComputerUpdated(computer);
                                    }
                                } catch (InterruptedException interrupted) {
                                    Thread.currentThread().interrupt();
                                    return;
                                } catch (java.util.concurrent.ExecutionException unavailable) {
                                    LimeLog.info("Current application status unavailable");
                                }
                            }
                            if (appList == null || Thread.currentThread() != thread) continue;
                            List<NvApp> list = NvHTTP.getAppListByReader(new StringReader(appList));
                            if (list.isEmpty()) {
                                LimeLog.warning("Empty app list received from "+computer.uuid);

                                // The app list might actually be empty, so if we get an empty response a few times
                                // in a row, we'll go ahead and believe it.
                                emptyAppListResponses++;
                            }
                            if (!appList.isEmpty() &&
                                    (!list.isEmpty() || emptyAppListResponses >= EMPTY_LIST_THRESHOLD)) {
                                // Open the cache file
                                try (final OutputStream cacheOut = CacheHelper.openCacheFileForOutput(
                                        getCacheDir(), "applist", computer.uuid)
                                ) {
                                    CacheHelper.writeStringToOutputStream(cacheOut, appList);
                                } catch (IOException e) {
                                    e.printStackTrace();
                                }

                                // Reset empty count if it wasn't empty this time
                                if (!list.isEmpty()) {
                                    emptyAppListResponses = 0;
                                }

                                // Update the computer
                                computer.rawAppList = appList;
                                receivedAppList = true;

                                // Notify that the app list has been updated
                                // and ensure that the thread is still active
                                ComputerManagerListener activeListener = listener;
                                if (activeListener != null && Thread.currentThread() == thread) {
                                    activeListener.notifyAppListUpdated(computer, list, verifiedRunningApp);
                                }
                            }
                            else if (appList.isEmpty()) {
                                LimeLog.warning("Null app list received from "+computer.uuid);
                            }
                        } catch (IOException | XmlPullParserException e) {
                            ComputerManagerListener activeListener = listener;
                            if (activeListener != null && Thread.currentThread() == thread)
                                activeListener.notifyAppListUnavailable(computer);
                            e.printStackTrace();
                        } finally {
                            if (checkRunning && verifiedRunningApp == null) {
                                refreshStatus.set(true);
                            }
                            if (runningApp != null) runningApp.cancel(true);
                        }
                    } while (waitPollingDelay());
                }
            };
            thread.setName("App list polling thread for " + computer.name);
            thread.start();
        }

        public synchronized void stop() {
            if (thread != null) {
                thread.interrupt();

                // Don't join here because we might be blocked on network I/O

                thread = null;
            }
        }
    }
}

class PollingTuple {
    public Thread thread;
    public final ComputerDetails computer;
    public final Object networkLock;
    public long lastSuccessfulPollMs;

    public PollingTuple(ComputerDetails computer, Thread thread) {
        this.computer = computer;
        this.thread = thread;
        this.networkLock = new Object();
    }
}

class ReachabilityTuple {
    public final String reachableAddress;
    public final ComputerDetails computer;

    public ReachabilityTuple(ComputerDetails computer, String reachableAddress) {
        this.computer = computer;
        this.reachableAddress = reachableAddress;
    }
}
