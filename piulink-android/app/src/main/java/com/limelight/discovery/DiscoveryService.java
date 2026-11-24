package com.limelight.discovery;

import java.util.List;

import com.limelight.nvstream.mdns.MdnsComputer;
import com.limelight.nvstream.mdns.JmDNSDiscoveryAgent;
import com.limelight.nvstream.mdns.MdnsDiscoveryAgent;
import com.limelight.nvstream.mdns.MdnsDiscoveryListener;
import com.limelight.nvstream.mdns.NsdManagerDiscoveryAgent;

import android.app.Service;
import android.content.Intent;
import android.os.Binder;
import android.os.Build;
import android.os.IBinder;

public class DiscoveryService extends Service {

    private MdnsDiscoveryAgent discoveryAgent;
    private MdnsDiscoveryListener boundListener;

    public class DiscoveryBinder extends Binder {
        public void setListener(MdnsDiscoveryListener listener) {
            boundListener = listener;
        }

        public void startDiscovery(int queryIntervalMs) {
            discoveryAgent.startDiscovery(queryIntervalMs);
        }

        public void stopDiscovery() {
            discoveryAgent.stopDiscovery();
        }

        public List<MdnsComputer> getComputerSet() {
            return discoveryAgent.getComputerSet();
        }
    }

    @Override
    public void onCreate() {
        MdnsDiscoveryListener listener = new MdnsDiscoveryListener() {
            @Override
            public void notifyComputerAdded(MdnsComputer computer) {
                if (boundListener != null) {
                    boundListener.notifyComputerAdded(computer);
                }
            }

            @Override
            public void notifyDiscoveryFailure(Exception e) {
                if (boundListener != null) {
                    boundListener.notifyDiscoveryFailure(e);
                }
            }
        };

        // NsdManager is useful in virtual Android environments where the platform proxies mDNS,
        // but several physical-device Android 14+ implementations fail to resolve host addresses
        // consistently. Use the MulticastLock-backed JmDNS path on physical devices so discovery
        // behaves the same across OS updates and vendor network stacks.
        boolean virtualAndroid = Build.FINGERPRINT.startsWith("generic") ||
                Build.FINGERPRINT.contains("emulator") ||
                Build.MODEL.contains("Android SDK built for") ||
                Build.MANUFACTURER.equalsIgnoreCase("Microsoft Corporation");
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE && virtualAndroid) {
            discoveryAgent = new NsdManagerDiscoveryAgent(getApplicationContext(), listener);
        }
        else {
            discoveryAgent = new JmDNSDiscoveryAgent(getApplicationContext(), listener);
        }
    }

    private final DiscoveryBinder binder = new DiscoveryBinder();

    @Override
    public IBinder onBind(Intent intent) {
        return binder;
    }

    @Override
    public boolean onUnbind(Intent intent) {
        // Stop any discovery session
        discoveryAgent.stopDiscovery();

        // Unbind the listener
        boundListener = null;
        return false;
    }
}
