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
        String route = routeFor(context, hostAddress);
        int downstreamEstimateKbps = getDownstreamEstimateKbps();
        int selectedKbps = AutoBitratePolicy.startupBitrate(route, width, height, fps,
                downstreamEstimateKbps, saveDataOnMeteredNetwork, tuningMode);
        return new Session(route, selectedKbps, downstreamEstimateKbps);
    }

    static String routeFor(Context context, String address) {
        ConnectivityManager manager = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        if (com.limelight.computers.DirectInviteTunnel.isEndpoint(address))
            return manager != null && manager.isActiveNetworkMetered() ? "metered" : "remote";
        return classifyRoute(address, NetHelper.isActiveNetworkVpn(context),
                manager != null && manager.isActiveNetworkMetered(), NetHelper.isPrivateHostAddress(address));
    }

    static String classifyRoute(String hostAddress, boolean privateNetworkActive,
                                boolean metered, boolean hostIsPrivate) {
        NetworkPathClassifier.Path path = NetworkPathClassifier.classify(hostAddress);
        return metered ? "metered" :
                path == NetworkPathClassifier.Path.TAILSCALE_MANAGED ? "private" :
                hostIsPrivate ? "local" : privateNetworkActive ? "private" : "remote";
    }

    private int getDownstreamEstimateKbps() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return 0;
        ConnectivityManager manager =
                (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        if (manager == null) return 0;
        Network activeNetwork = manager.getActiveNetwork();
        if (activeNetwork == null) return 0;
        NetworkCapabilities capabilities = manager.getNetworkCapabilities(activeNetwork);
        return capabilities != null ? capabilities.getLinkDownstreamBandwidthKbps() : 0;
    }

    public static final class Session {
        private final String route;
        private final int selectedKbps;
        private final int downstreamEstimateKbps;
        private final ThroughputHistory throughputHistory = new ThroughputHistory();
        private final AutoBitratePolicy.Feedback feedback = new AutoBitratePolicy.Feedback();

        private Session(String route, int selectedKbps, int downstreamEstimateKbps) {
            this.route = route;
            this.selectedKbps = selectedKbps;
            this.downstreamEstimateKbps = downstreamEstimateKbps;
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
