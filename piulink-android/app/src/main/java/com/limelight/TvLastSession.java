package com.limelight;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;

import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.PairingManager;
import java.util.List;
import java.util.Set;

/** The last TV stream to complete its handshake, keyed by the paired Host UUID. */
final class TvLastSession {
    final String host;
    final int appId;
    final String appName;

    private TvLastSession(SharedPreferences prefs) {
        host = prefs.getString("host", "");
        appId = prefs.getInt("appId", 0);
        appName = prefs.getString("appName", "");
    }

    static TvLastSession read(Context context) {
        return new TvLastSession(context.getSharedPreferences("TvLastSession", Context.MODE_PRIVATE));
    }

    enum HomeAction { CHECK, OFFLINE, PAIR, CONTINUE }

    static boolean isHomeHost(String lastHost, String candidate, int hostCount) {
        return lastHost.isEmpty() ? hostCount == 1 : lastHost.equalsIgnoreCase(candidate);
    }

    static HomeAction homeAction(ComputerDetails.State state, boolean hasCertificate,
                                 PairingManager.PairState pairing) {
        if (state == ComputerDetails.State.OFFLINE) return HomeAction.OFFLINE;
        if (state != ComputerDetails.State.ONLINE) return HomeAction.CHECK;
        if (!hasCertificate || pairing != PairingManager.PairState.PAIRED) return HomeAction.PAIR;
        return HomeAction.CONTINUE;
    }

    static NvApp publishedApp(int id, List<NvApp> catalog, Set<Integer> hidden) {
        if (id <= 0 || hidden.contains(id)) return null;
        for (NvApp app : catalog) if (app.getAppId() == id) return app;
        return null;
    }

    static void connected(Context context, Intent stream) {
        String host = stream.getStringExtra(Game.EXTRA_PC_UUID);
        int appId = stream.getIntExtra(Game.EXTRA_APP_ID, 0);
        if (host == null || appId <= 0) return;
        context.getSharedPreferences("TvLastSession", Context.MODE_PRIVATE).edit()
                .putString("host", host).putInt("appId", appId)
                .putString("appName", stream.getStringExtra(Game.EXTRA_APP_NAME)).apply();
    }

    static void forget(Context context, String host) {
        if (read(context).host.equals(host)) {
            context.getSharedPreferences("TvLastSession", Context.MODE_PRIVATE).edit().clear().apply();
        }
    }
}
