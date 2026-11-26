package com.limelight.utils;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkInfo;
import android.net.LinkProperties;
import android.net.LinkAddress;
import android.net.RouteInfo;
import android.os.Build;

import com.limelight.LimeLog;
import com.limelight.nvstream.http.NvHTTP;

import java.net.InetAddress;
import java.net.UnknownHostException;

public class NetHelper {
    public static final String DIRECT_INVITE_NETWORK = "checkbox_direct_invite_network";

    public static boolean preferPhysicalInviteNetwork(Context context) {
        return android.preference.PreferenceManager.getDefaultSharedPreferences(context)
                .getBoolean(DIRECT_INVITE_NETWORK, false);
    }

    /** Choose an already active Internet bearer only when Android permits binding to it. */
    public static Network allowedInviteNetwork(Context context) {
        ConnectivityManager cm = context.getSystemService(ConnectivityManager.class);
        if (cm == null || !preferPhysicalInviteNetwork(context) || !isActiveNetworkVpn(context)) return null;
        Network[] networks = cm.getAllNetworks();
        for (int transport : new int[]{NetworkCapabilities.TRANSPORT_ETHERNET, NetworkCapabilities.TRANSPORT_WIFI,
                NetworkCapabilities.TRANSPORT_CELLULAR}) {
            for (Network network : networks) {
                NetworkCapabilities caps = cm.getNetworkCapabilities(network);
                LinkProperties link = cm.getLinkProperties(network);
                if (caps == null || !caps.hasTransport(transport) || caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
                        || !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                        || link == null || link.getInterfaceName() == null) continue;
                try (java.net.DatagramSocket probe = new java.net.DatagramSocket(null)) {
                    network.bindSocket(probe);
                    return network;
                } catch (java.io.IOException | SecurityException denied) { /* Keep the system route if restricted. */ }
            }
        }
        return null;
    }
    public static boolean isPrivateHostAddress(String host) {
        if (host == null || host.trim().isEmpty()) {
            return false;
        }

        String numericHost = host.trim();
        if (numericHost.startsWith("[") && numericHost.endsWith("]")) {
            numericHost = numericHost.substring(1, numericHost.length() - 1);
        }

        // Avoid DNS work on the UI thread. Hostnames on a private overlay are already
        // classified by the active VPN flag; this parser is only for literal IP addresses.
        if (!numericHost.matches("[0-9.]+") && !numericHost.contains(":")) {
            return false;
        }

        try {
            InetAddress address = InetAddress.getByName(numericHost);
            if (address.isAnyLocalAddress() || address.isLoopbackAddress() ||
                    address.isLinkLocalAddress() || address.isSiteLocalAddress()) {
                return true;
            }

            byte[] bytes = address.getAddress();
            if (bytes.length == 4) {
                int first = bytes[0] & 0xff;
                int second = bytes[1] & 0xff;
                return first == 100 && second >= 64 && second <= 127;
            }

            // IPv6 unique-local addresses (fc00::/7), including Tailscale's fd7a::/48.
            return bytes.length == 16 && (bytes[0] & 0xfe) == 0xfc;
        }
        catch (Exception e) {
            LimeLog.warning("Unable to classify host address: " + e.getMessage());
            return false;
        }
    }

    public static boolean shouldPreferLocalAddress(boolean hasDirectLanRoute, boolean activeNetworkIsVpn) {
        return hasDirectLanRoute || !activeNetworkIsVpn;
    }

    /**
     * Returns true when Android has a direct Wi-Fi or Ethernet route to the host.
     * This deliberately inspects every network rather than only the active network,
     * because a third-party VPN can be Android's active network while the phone and
     * host are still on the same LAN.
     */
    public static boolean isAddressOnLocalNetwork(Context context, String address) {
        return getLocalNetworkForAddress(context, address) != null;
    }

    /**
     * Returns the Wi-Fi or Ethernet network that owns the direct route to this
     * address. This is also used to bind sockets, since some Android builds send
     * private LAN traffic through cellular data when a VPN is active.
     */
    public static Network getLocalNetworkForAddress(Context context, String address) {
        if (address == null || address.isEmpty()) {
            return null;
        }

        final InetAddress hostAddress;
        try {
            hostAddress = InetAddress.getByName(address);
        } catch (UnknownHostException e) {
            return null;
        }

        if (hostAddress.isLoopbackAddress() || hostAddress.isAnyLocalAddress()) return null;
        ConnectivityManager connMgr =
                (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        if (connMgr == null) {
            return null;
        }

        for (Network network : connMgr.getAllNetworks()) {
            NetworkCapabilities capabilities = connMgr.getNetworkCapabilities(network);
            if (capabilities == null || capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN) ||
                    (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) &&
                     !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET))) {
                continue;
            }

            LinkProperties properties = connMgr.getLinkProperties(network);
            if (properties == null) {
                continue;
            }

            // Some devices only expose the Wi-Fi default route while a VPN is
            // active.  The default route alone cannot prove that the host is
            // local, but a match against the interface's own prefix can.
            for (LinkAddress linkAddress : properties.getLinkAddresses()) {
                if (isAddressInLinkPrefix(hostAddress, linkAddress)) {
                    return network;
                }
            }

            for (RouteInfo route : properties.getRoutes()) {
                // A default route matches every address and says nothing about LAN reachability.
                if (Build.VERSION.SDK_INT >= 33 && route.getType() != RouteInfo.RTN_UNICAST) continue;
                InetAddress gateway = route.getGateway();
                if (!route.isDefaultRoute() && (gateway == null || gateway.isAnyLocalAddress()) && route.matches(hostAddress)) {
                    return network;
                }
            }
        }

        return null;
    }

    private static boolean isAddressInLinkPrefix(InetAddress address, LinkAddress linkAddress) {
        byte[] addressBytes = address.getAddress();
        byte[] networkBytes = linkAddress.getAddress().getAddress();
        int prefixLength = linkAddress.getPrefixLength();
        if (addressBytes.length != networkBytes.length || prefixLength <= 0 ||
                prefixLength > addressBytes.length * Byte.SIZE) {
            return false;
        }

        int wholeBytes = prefixLength / Byte.SIZE;
        for (int i = 0; i < wholeBytes; i++) {
            if (addressBytes[i] != networkBytes[i]) {
                return false;
            }
        }

        int remainingBits = prefixLength % Byte.SIZE;
        if (remainingBits == 0) {
            return true;
        }

        int mask = (0xff << (Byte.SIZE - remainingBits)) & 0xff;
        return (addressBytes[wholeBytes] & mask) == (networkBytes[wholeBytes] & mask);
    }

    private static volatile long deniedDirectNetworkHandle;

    public static void noteDirectNetworkBindingDenied(Network network) {
        if (network != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            deniedDirectNetworkHandle = network.getNetworkHandle();
        }
    }

    static boolean isDirectNetworkHandleDenied(long networkHandle) {
        return networkHandle != 0 && networkHandle == deniedDirectNetworkHandle;
    }

    static void setDeniedDirectNetworkHandleForTest(long networkHandle) {
        deniedDirectNetworkHandle = networkHandle;
    }

    public static void resetDirectNetworkBindingState() {
        deniedDirectNetworkHandle = 0;
    }

    public static Network getBindableLocalNetworkForAddress(Context context, String address) {
        if (!isActiveNetworkVpn(context)) {
            return null;
        }
        Network network = getLocalNetworkForAddress(context, address);
        if (network != null && Build.VERSION.SDK_INT >= Build.VERSION_CODES.M &&
                isDirectNetworkHandleDenied(network.getNetworkHandle())) {
            return null;
        }
        return network;
    }

    public static Network bindToDirectLocalNetworkWithNetwork(Context context, NvHTTP http, String address) {
        // The default route already reaches the LAN when Wi-Fi/Ethernet is active.
        // Some vendor Android builds reject Network-bound sockets with EPERM even
        // with CHANGE_NETWORK_STATE granted, so only override routing when a VPN
        // actually owns the process default network and binding has not failed.
        Network network = getBindableLocalNetworkForAddress(context, address);
        if (network != null) {
            http.setSocketFactory(network.getSocketFactory());
            LimeLog.info("Bound host request for " + address + " to direct Wi-Fi/Ethernet network");
            return network;
        }
        return null;
    }

    public static boolean bindToDirectLocalNetwork(Context context, NvHTTP http, String address) {
        return bindToDirectLocalNetworkWithNetwork(context, http, address) != null;
    }

    /**
     * Some vendor Android builds expose a VPN's Wi-Fi underlay through getAllNetworks(),
     * but reject app socket binding to that underlay with EPERM. The ordinary process
     * route can still reach the LAN, so callers should retry without the bound factory.
     */
    public static boolean isNetworkBindingDenied(Throwable error) {
        for (Throwable cause = error; cause != null; cause = cause.getCause()) {
            String message = cause.getMessage();
            if (message != null &&
                    (message.contains("EPERM") || message.contains("Operation not permitted"))) {
                return true;
            }
        }
        return false;
    }

    /**
     * Strips surrounding URI brackets and any IPv6 scope zone (for example "%wlan0")
     * so addresses remain valid in both sockets and HTTP URL authorities.
     */
    public static String stripIpv6Zone(String host) {
        if (host == null) {
            return "";
        }
        String clean = host.trim();
        if (clean.startsWith("[") && clean.endsWith("]") && clean.length() > 2) {
            clean = clean.substring(1, clean.length() - 1);
        }
        int zone = clean.indexOf('%');
        if (zone >= 0 && clean.contains(":")) {
            clean = clean.substring(0, zone);
        }
        return clean;
    }

    /** Formats a host literal or hostname safely for a URL authority segment. */
    public static String formatAuthorityHost(String host) {
        String clean = stripIpv6Zone(host);
        return clean.contains(":") ? "[" + clean + "]" : clean;
    }

    public static boolean isActiveNetworkVpn(Context context) {
        ConnectivityManager connMgr = (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Network activeNetwork = connMgr.getActiveNetwork();
            if (activeNetwork != null) {
                NetworkCapabilities netCaps = connMgr.getNetworkCapabilities(activeNetwork);
                if (netCaps != null) {
                    return netCaps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) ||
                            !netCaps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN);
                }
            }
        }
        else {
            NetworkInfo activeNetworkInfo = connMgr.getActiveNetworkInfo();
            if (activeNetworkInfo != null) {
                return activeNetworkInfo.getType() == ConnectivityManager.TYPE_VPN;
            }
        }

        return false;
    }
}
