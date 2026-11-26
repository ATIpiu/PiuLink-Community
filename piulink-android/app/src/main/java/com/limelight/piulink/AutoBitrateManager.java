package com.limelight.piulink;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.os.Build;
import com.limelight.utils.NetHelper;

public final class AutoBitrateManager {
    private final Context context;

    public AutoBitrateManager(Context context) {
        this.context = context.getApplicationContext();
    }

    public Session beginSession(String hostAddress, boolean saveDataOnMeteredNetwork,
                                int width, int height, int fps, String tuningMode) {
        StreamPath path = streamPath(context, hostAddress);
        int selectedKbps = AutoBitratePolicy.startupBitrate(path.route, width, height, fps,
                path.downstreamKbps, saveDataOnMeteredNetwork, tuningMode);
        return new Session(path, selectedKbps);
    }

    static String routeFor(Context context, String address) {
        return streamPath(context, address).route;
    }

    private static final class StreamPath {
        final String route;
        final int downstreamKbps;
        final boolean vpnTransport;

        StreamPath(String route, int downstreamKbps, boolean vpnTransport) {
            this.route = route;
            this.downstreamKbps = downstreamKbps;
            this.vpnTransport = vpnTransport;
        }
    }

    private static StreamPath streamPath(Context context, String address) {
        ConnectivityManager manager = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        boolean vpn = NetHelper.isActiveNetworkVpn(context);
        boolean tunnel = com.limelight.computers.DirectInviteTunnel.isEndpoint(address);
        boolean literal = address != null && (address.contains(":") || address.matches("[0-9.]+"));
        // Do not perform DNS on the UI thread. A name without verified local routing
        // uses the VPN budget until a concrete on-link endpoint is selected.
        Network lan = !tunnel && literal && vpn ? NetHelper.getBindableLocalNetworkForAddress(context, address) : null;
        Network network = lan != null ? lan : manager == null || Build.VERSION.SDK_INT < 23 ? null : manager.getActiveNetwork();
        NetworkCapabilities caps = manager == null || network == null ? null : manager.getNetworkCapabilities(network);
        boolean metered = caps != null ? !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
                : manager != null && manager.isActiveNetworkMetered();
        boolean local = !tunnel && (vpn ? lan != null : NetHelper.isPrivateHostAddress(address));
        String route = tunnel ? (metered ? "metered" : "remote") : classifyRoute(address, vpn, metered, local);
        return new StreamPath(route, caps == null ? 0 : caps.getLinkDownstreamBandwidthKbps(), vpn && lan == null);
    }

    static String classifyRoute(String hostAddress, boolean vpnActive, boolean metered, boolean directLocal) {
        if (metered) return "metered";
        if (NetworkPathClassifier.classify(hostAddress) == NetworkPathClassifier.Path.TAILSCALE_MANAGED) return "private";
        if (directLocal) return "local";
        return vpnActive ? "private" : "remote";
    }

    public static final class Session {
        private final String route;
        private final int selectedKbps;
        private final int downstreamEstimateKbps;
        private final ThroughputHistory throughputHistory = new ThroughputHistory();
        private final AutoBitratePolicy.Feedback feedback;

        private Session(StreamPath path, int selectedKbps) {
            this.route = path.route;
            this.selectedKbps = selectedKbps;
            this.downstreamEstimateKbps = path.downstreamKbps;
            this.feedback = new AutoBitratePolicy.Feedback(path.vpnTransport);
        }

        public int getSelectedKbps() { return selectedKbps; }
        public int getCeilingKbps() { return selectedKbps; }
        public String getRoute() { return route; }
        public int getDownstreamEstimateKbps() { return downstreamEstimateKbps; }

        public int getHistoricalVideoKbps() {
            throughputHistory.prune(android.os.SystemClock.elapsedRealtime());
            return throughputHistory.lowerQuartileKbps();
        }

        public boolean hasHistoricalVideoThroughput() {
            throughputHistory.prune(android.os.SystemClock.elapsedRealtime());
            return throughputHistory.size() >= 3;
        }

        public void recordThroughputSample(int receivedKbps, long nowMs) {
            throughputHistory.add(nowMs, receivedKbps);
        }

        public int nextEncoderTarget(int applied, int received, int lost, int frames, int rttMs, long nowMs) {
            return feedback.nextEncoderTarget(applied, received, lost, frames, rttMs, nowMs);
        }
    }
}
