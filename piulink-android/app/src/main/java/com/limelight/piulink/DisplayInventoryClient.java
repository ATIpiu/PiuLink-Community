package com.limelight.piulink;

import android.content.Context;
import android.net.Network;

import com.limelight.utils.NetHelper;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.BufferedInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ConcurrentHashMap;

final class DisplayInventoryClient {
    static final class StreamActiveException extends IOException {
        final boolean recovering;
        StreamActiveException(boolean recovering) {
            super(recovering ? "display_recovering" : "stream_active");
            this.recovering = recovering;
        }
    }
    static final int PORT = 48011;
    private static final int MAX_RESPONSE_BYTES = 128 * 1024;
    private static final int PEER_CONNECT_TIMEOUT_MS = 3000;
    private static final int PEER_RETRY_DELAY_MS = 250;
    // Device IDs and capture indices are stable for a running Host. Failed preparation
    // invalidates this cache, so normal launches avoid a second control round-trip.
    private static final long INVENTORY_CACHE_MS = 8000;
    private static final ConcurrentHashMap<String, CachedInventory> inventoryCache = new ConcurrentHashMap<>();

    private static final class CachedInventory {
        Inventory inventory;
        long storedAt;
        java.util.concurrent.FutureTask<Inventory> pending;
    }

    static final class Display {
        final int captureIndex;
        final String deviceId;
        final String name;
        final boolean primary;
        final boolean virtual;
        final int width;
        final int height;
        DesktopLayout.Bounds bounds;
        final boolean selected;
        final int scalePercent;
        final List<Integer> scaleOptions;
        final List<Mode> modes = new ArrayList<>();
        boolean isActive() { return bounds != null && width > 0 && height > 0; }

        Display(int captureIndex, String deviceId, String name, boolean primary,
                boolean virtual, int width, int height, boolean selected) {
            this(captureIndex, deviceId, name, primary, virtual, width, height, selected,
                    0, new ArrayList<>());
        }

        Display(int captureIndex, String deviceId, String name, boolean primary,
                boolean virtual, int width, int height, boolean selected,
                int scalePercent, List<Integer> scaleOptions) {
            this.captureIndex = captureIndex;
            this.deviceId = deviceId;
            this.name = name;
            this.primary = primary;
            this.virtual = virtual;
            this.width = width;
            this.height = height;
            this.selected = selected;
            this.scalePercent = scalePercent;
            this.scaleOptions = scaleOptions;
        }
    }

    static final class Inventory {
        final boolean exclusive;
        final List<Display> displays;
        final List<Resolution> virtualResolutions;
        final List<Integer> virtualRefreshRates;
        boolean supportsSessionDisplays;
        boolean supportsWindowGathering;
        boolean supportsDisplayScaling;

        Inventory(boolean exclusive, List<Display> displays, List<Resolution> virtualResolutions,
                  List<Integer> virtualRefreshRates) {
            this.exclusive = exclusive;
            this.displays = displays;
            this.virtualResolutions = virtualResolutions;
            this.virtualRefreshRates = virtualRefreshRates;
        }
    }

    static final class Resolution {
        final int width;
        final int height;

        Resolution(int width, int height) {
            this.width = width;
            this.height = height;
        }

        String id() { return width + "x" + height; }
    }

    private DisplayInventoryClient() { }

    static final class Mode {
        final int width, height, fps;
        Mode(int width, int height, int fps) { this.width = width; this.height = height; this.fps = fps; }
        String resolution() { return width + "x" + height; }
        @Override public String toString() { return width + " × " + height + " · " + fps + " Hz"; }
    }

    static Display defaultDisplay(Inventory inventory, String saved, boolean virtualApp) {
        if (!saved.isEmpty()) {
            for (Display display : inventory.displays)
                if (display.deviceId.equalsIgnoreCase(saved) && (!virtualApp || display.virtual) &&
                        (display.virtual || (display.width > 0 && display.height > 0))) return display;
            return null;
        }
        if (virtualApp) {
            for (Display display : inventory.displays) if (display.virtual) return display;
            return null;
        }
        return activeDesktop(inventory);
    }

    static Display activeDesktop(Inventory inventory) {
        Display physical = null, primary = null, virtualDesktop = null;
        for (Display display : inventory.displays) {
            if (display.width <= 0 || display.height <= 0) continue;
            if (display.virtual) {
                if (virtualDesktop == null || display.selected) virtualDesktop = display;
            } else {
                if (display.selected) return display;
                if (display.primary) primary = display;
                if (physical == null) physical = display;
            }
        }
        return primary != null ? primary : physical != null ? physical : virtualDesktop;
    }

    static Inventory fetch(Context context, String host) throws Exception {
        return fetchInventory(context, host, true);
    }

    static Inventory fetchCached(Context context, String host) throws Exception {
        return fetchInventory(context, host, false);
    }

    private static Inventory fetchInventory(Context context, String host, boolean refresh) throws Exception {
        if (host == null || host.trim().isEmpty()) throw new IOException("Host address is missing");
        CachedInventory newSlot = new CachedInventory();
        CachedInventory previous = inventoryCache.putIfAbsent(host, newSlot);
        CachedInventory slot = previous == null ? newSlot : previous;
        java.util.concurrent.FutureTask<Inventory> read;
        synchronized (slot) {
            if (!refresh && slot.inventory != null && android.os.SystemClock.elapsedRealtime() - slot.storedAt <= INVENTORY_CACHE_MS)
                return slot.inventory;
            if (slot.pending == null) slot.pending = new java.util.concurrent.FutureTask<>(() -> {
                Inventory result;
                try { result = fetchWithNetworkFallback(context, host); }
                catch (Exception firstFailure) {
                    // A cold Tailscale route can become reachable on the second read.
                    Thread.sleep(PEER_RETRY_DELAY_MS);
                    result = fetchWithNetworkFallback(context, host);
                }
                synchronized (slot) {
                    slot.inventory = result;
                    slot.storedAt = android.os.SystemClock.elapsedRealtime();
                }
                return result;
            });
            read = slot.pending;
        }
        read.run(); // FutureTask runs the network request once, even if a launch joins prewarming.
        try { return read.get(); }
        catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); throw interrupted; }
        catch (java.util.concurrent.ExecutionException wrapped) {
            if (wrapped.getCause() instanceof Exception) throw (Exception) wrapped.getCause();
            throw new IOException("Cannot read display inventory", wrapped.getCause());
        }
        finally { synchronized (slot) { if (slot.pending == read && read.isDone()) slot.pending = null; } }
    }

    static boolean lastInventoryWasLegacy(String host) {
        CachedInventory slot = host == null ? null : inventoryCache.get(host);
        if (slot == null) return false;
        synchronized (slot) { return slot.inventory != null && !slot.inventory.supportsSessionDisplays; }
    }

    static void invalidate(String host) {
        if (host != null) inventoryCache.remove(host);
    }

    private static Inventory fetchWithNetworkFallback(Context context, String host) throws Exception {
        Network directNetwork = NetHelper.getBindableLocalNetworkForAddress(context, NetHelper.stripIpv6Zone(host));
        try {
            return fetchOnce(context, host, directNetwork);
        } catch (Exception directNetworkFailure) {
            // Xiaomi and some VPN builds expose the Wi-Fi route but reject a socket that is
            // explicitly bound to it. This mirrors the pairing/app-list fallback: only in that
            // case should the inventory request retry on Android's ordinary default route.
            if (directNetwork == null || !NetHelper.isNetworkBindingDenied(directNetworkFailure)) throw directNetworkFailure;
            NetHelper.noteDirectNetworkBindingDenied(directNetwork);
            return fetchOnce(context, host, null);
        }
    }

    private static Inventory fetchOnce(Context context, String host, Network directNetwork) throws Exception {
        if (host == null || host.trim().isEmpty()) throw new IOException("Host address is missing");
        String urlHost = NetHelper.formatAuthorityHost(host);
        URL url = new URL("https://" + urlHost + ":" + PORT + "/v1/displays");
        HttpURLConnection connection = (HttpURLConnection)(directNetwork == null
                ? url.openConnection() : directNetwork.openConnection(url));
        configureTls(context, host, connection);
        connection.setRequestMethod("GET");
        connection.setConnectTimeout(PEER_CONNECT_TIMEOUT_MS);
        connection.setReadTimeout(5000);
        connection.setUseCaches(false);
        connection.setInstanceFollowRedirects(false);
        connection.setRequestProperty("Accept", "application/json");
        try {
            int status = connection.getResponseCode();
            if (status == HttpURLConnection.HTTP_FORBIDDEN) throw new IOException("Host 拒绝了设备身份，请检查配对记录。");
            if (status == HttpURLConnection.HTTP_UNAVAILABLE) throw new IOException("Host 串流服务暂不可用，请在电脑上检查引擎状态。");
            if (status != HttpURLConnection.HTTP_OK) throw new IOException("Host 请求失败（HTTP " + status + "）。");
            byte[] payload;
            try (BufferedInputStream input = new BufferedInputStream(connection.getInputStream());
                 ByteArrayOutputStream output = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[4096];
                int total = 0;
                int read;
                while ((read = input.read(buffer)) != -1) {
                    total += read;
                    if (total > MAX_RESPONSE_BYTES) throw new IOException("Display response is too large");
                    output.write(buffer, 0, read);
                }
                payload = output.toByteArray();
            }

            return parseInventory(new String(payload, StandardCharsets.UTF_8));
        } finally {
            connection.disconnect();
        }
    }

    static Inventory parseInventory(String json) throws Exception {
            JSONObject root = new JSONObject(json);
            JSONArray values = root.getJSONArray("displays");
            List<Display> displays = new ArrayList<>();
            for (int i = 0; i < values.length(); i++) {
                JSONObject value = values.getJSONObject(i);
                int captureIndex = value.getInt("captureIndex");
                if (MonitorSelectionPolicy.normalize(captureIndex) < 0) continue;
                String deviceId = value.optString("deviceId", "").trim();
                String name = value.optString("name", "").trim();
                if (deviceId.isEmpty()) continue;
                if (name.isEmpty()) name = value.optString("windowsName", "").trim();
                if (name.isEmpty()) name = "Display " + (captureIndex + 1);
                List<Integer> scaleOptions = new ArrayList<>();
                JSONArray scaleValues = value.optJSONArray("scaleOptions");
                if (scaleValues != null) {
                    for (int optionIndex = 0; optionIndex < scaleValues.length(); optionIndex++) {
                        int option = scaleValues.optInt(optionIndex, 0);
                        if (isWindowsScale(option) && !scaleOptions.contains(option)) scaleOptions.add(option);
                    }
                }
                int scalePercent = value.optInt("scalePercent", 0);
                if (!isWindowsScale(scalePercent)) scalePercent = 0;
                displays.add(new Display(captureIndex, deviceId, name,
                        value.optBoolean("isPrimary"), value.optBoolean("isVirtual"),
                        value.optInt("width", 0), value.optInt("height", 0),
                        value.optBoolean("isSelected"), scalePercent, scaleOptions));
                JSONObject rect = value.optJSONObject("bounds");
                if (rect != null && rect.has("x") && rect.has("y") && rect.optInt("width") > 0 && rect.optInt("height") > 0)
                    displays.get(displays.size() - 1).bounds = new DesktopLayout.Bounds(
                            rect.getInt("x"), rect.getInt("y"), rect.getInt("width"), rect.getInt("height"));
                JSONArray actualModes = value.optJSONArray("modes");
                if (actualModes != null) for (int index = 0; index < actualModes.length(); index++) {
                    JSONObject mode = actualModes.getJSONObject(index);
                    int w = mode.optInt("width"), h = mode.optInt("height"), fps = mode.optInt("fps");
                    if (w >= 640 && h >= 360 && fps >= 30 && fps <= 480)
                        displays.get(displays.size() - 1).modes.add(new Mode(w, h, fps));
                }
            }
            List<Resolution> virtualResolutions = new ArrayList<>();
            JSONArray modes = root.optJSONArray("virtualResolutions");
            if (modes != null) {
                for (int i = 0; i < modes.length(); i++) {
                    JSONObject mode = modes.optJSONObject(i);
                    if (mode == null) continue;
                    int width = mode.optInt("width", 0);
                    int height = mode.optInt("height", 0);
                    if (width >= 640 && height >= 360) {
                        virtualResolutions.add(new Resolution(width, height));
                    }
                }
            }
            List<Integer> virtualRefreshRates = new ArrayList<>();
            JSONArray rates = root.optJSONArray("virtualRefreshRates");
            if (rates != null) {
                for (int i = 0; i < rates.length(); i++) {
                    int rate = rates.optInt(i, 0);
                    if (rate >= 30 && rate <= 360) virtualRefreshRates.add(rate);
                }
            }
            Inventory inventory = new Inventory(root.optBoolean("exclusiveDisplay"), displays,
                    virtualResolutions, virtualRefreshRates);
            inventory.supportsSessionDisplays = root.optBoolean("supportsSessionDisplays");
            inventory.supportsWindowGathering = root.optBoolean("supportsWindowGathering", false);
            inventory.supportsDisplayScaling = root.optBoolean("supportsDisplayScaling", false);
            return inventory;
    }

    private static boolean isWindowsScale(int value) {
        return value == 100 || value == 125 || value == 150 || value == 175 ||
                value == 200 || value == 225 || value == 250 || value == 300 ||
                value == 350 || value == 400 || value == 450 || value == 500;
    }

    static JSONObject buildSelection(String deviceId, boolean exclusive, int width, int height) throws Exception {
        return buildSelection(deviceId, exclusive, width, height, 0);
    }

    static JSONObject buildSelection(String deviceId, boolean exclusive, int width, int height,
                                     int scalePercent) throws Exception {
        if (width < 0 || height < 0 || ((width == 0) != (height == 0)) || (!exclusive && width != 0))
            throw new IOException("Invalid display resolution");
        if (exclusive && (deviceId == null || deviceId.trim().isEmpty()))
            throw new IOException("Virtual display ID required");
        if (scalePercent != 0 && ((deviceId == null || deviceId.trim().isEmpty()) ||
                !isWindowsScale(scalePercent))) throw new IOException("Invalid Windows display scale");
        JSONObject request = new JSONObject();
        request.put("deviceId", deviceId == null ? JSONObject.NULL : deviceId);
        request.put("exclusive", exclusive);
        if (width > 0 && height > 0) {
            request.put("width", width);
            request.put("height", height);
            request.put("fps", 60);
        }
        if (scalePercent > 0) request.put("scalePercent", scalePercent);
        return request;
    }

    static void validateSelectionResponse(String json) throws Exception {
        JSONObject result = new JSONObject(json);
        if (!"ready".equals(result.optString("status"))) throw new IOException("Host did not confirm display readiness");
    }
    static void select(Context context, String host, String deviceId, boolean exclusive,
                       int width, int height, boolean gatherWindows,
                       int scalePercent, int fps, String requestId) throws Exception {
        Network network = NetHelper.getBindableLocalNetworkForAddress(context, NetHelper.stripIpv6Zone(host));
        try { selectOnce(context, host, deviceId, exclusive, width, height, gatherWindows, scalePercent, fps, requestId, network); }
        catch (Exception failure) {
            if (network == null || !NetHelper.isNetworkBindingDenied(failure)) throw failure;
            NetHelper.noteDirectNetworkBindingDenied(network);
            selectOnce(context, host, deviceId, exclusive, width, height, gatherWindows, scalePercent, fps, requestId, null);
        }
    }

    private static void selectOnce(Context context, String host, String deviceId, boolean exclusive,
                       int width, int height, boolean gatherWindows,
                       int scalePercent, int fps, String requestId, Network network) throws Exception {
        if (host == null || host.trim().isEmpty()) throw new IOException("Host address is missing");
        String urlHost = NetHelper.formatAuthorityHost(host);
        URL url = new URL("https://" + urlHost + ":" + PORT + "/v1/displays/select");
        HttpURLConnection connection = (HttpURLConnection)(network == null ? url.openConnection() : network.openConnection(url));
        configureTls(context, host, connection);
        connection.setRequestMethod("POST");
        connection.setConnectTimeout(PEER_CONNECT_TIMEOUT_MS);
        connection.setReadTimeout(25000);
        connection.setUseCaches(false);
        connection.setInstanceFollowRedirects(false);
        connection.setDoOutput(true);
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        JSONObject request = buildSelection(deviceId, exclusive, width, height, scalePercent);
        request.put("requestId", requestId);
        if (exclusive && width > 0) request.put("fps", fps > 0 ? fps : 60);
        if (gatherWindows) request.put("gatherWindows", true);
        byte[] payload = request.toString().getBytes(StandardCharsets.UTF_8);
        connection.setFixedLengthStreamingMode(payload.length);
        try {
            try (OutputStream output = connection.getOutputStream()) {
                output.write(payload);
            }
            int status = connection.getResponseCode();
            if (status == HttpURLConnection.HTTP_CONFLICT) {
                String reason = "";
                try (java.io.InputStream body = connection.getErrorStream()) {
                    if (body != null) {
                        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                        byte[] buffer = new byte[256];
                        int count;
                        while ((count = body.read(buffer)) != -1 && bytes.size() + count <= 2048)
                            bytes.write(buffer, 0, count);
                        reason = new JSONObject(bytes.toString("UTF-8")).optString("error");
                    }
                } catch (org.json.JSONException ignored) { }
                if (reason.equals("display_recovery_failed"))
                    throw new IOException("请在电脑 Host 的显示设置中点击“修复桌面布局”，再重新连接。");
                if (reason.equals("retained_application"))
                    throw new IOException("电脑仍保留原应用。可用原屏幕恢复，或先结束应用再更换屏幕。");
                throw new StreamActiveException("display_recovering".equals(reason));
            }
            if (status == HttpURLConnection.HTTP_FORBIDDEN) throw new IOException("Host 拒绝了设备身份，请检查配对记录。");
            if (status == HttpURLConnection.HTTP_UNAVAILABLE) throw new IOException("Host 串流服务暂不可用，请在电脑上检查引擎状态。");
            if (status != HttpURLConnection.HTTP_OK) throw new IOException("Host 请求失败（HTTP " + status + "）。");
            try (BufferedInputStream input = new BufferedInputStream(connection.getInputStream());
                 ByteArrayOutputStream output = new ByteArrayOutputStream()) {
                byte[] buffer = new byte[512]; int count;
                while ((count = input.read(buffer)) != -1) {
                    if (output.size() + count > 4096) throw new IOException("Invalid preparation response");
                    output.write(buffer, 0, count);
                }
                validateSelectionResponse(new String(output.toByteArray(), StandardCharsets.UTF_8));
            }
        } finally {
            connection.disconnect();
        }
    }

    static final class SessionError extends IOException {
        final String reason;
        SessionError(String reason) { super(reason); this.reason = reason; }
    }

    static JSONObject sessionDisplays(Context context, String host, String sessionId, String action,
                                      String key, int expectedCount, boolean automaticRead) throws Exception {
        JSONObject request = new JSONObject().put("sessionId", sessionId).put("action", action);
        if (key != null) request.put("key", key);
        if (expectedCount >= 0) request.put("expectedCount", expectedCount);
        JSONObject result = postSession(context, host, "/v1/displays/session", request,
                automaticRead ? PEER_CONNECT_TIMEOUT_MS : 15000, null);
        invalidate(host);
        return result;
    }

    static boolean inputStatus(Context context, String host, String sessionId, String action, com.limelight.nvstream.http.NvHTTP paired) throws Exception {
        return inputGeometry(context, host, sessionId, action, paired).getBoolean("active");
    }

    static JSONObject inputGeometry(Context context, String host, String sessionId, String action, com.limelight.nvstream.http.NvHTTP paired) throws Exception {
        JSONObject request = new JSONObject().put("sessionId", sessionId).put("action", action);
        return postSession(context, host, "/v1/displays/input-status", request, 2200, paired);
    }

    private static JSONObject postSession(Context context, String host, String path, JSONObject request,
                                          int timeout, com.limelight.nvstream.http.NvHTTP paired) throws Exception {
        byte[] body = request.toString().getBytes(StandardCharsets.UTF_8);
        String cleanHost = NetHelper.stripIpv6Zone(host);
        String address = NetHelper.formatAuthorityHost(cleanHost);
        URL url = new URL("https://" + address + ":" + PORT + path);
        Network network = NetHelper.getBindableLocalNetworkForAddress(context, cleanHost);
        for (;;) {
            HttpURLConnection connection = (HttpURLConnection)(network == null ? url.openConnection() : network.openConnection(url));
            boolean writing = false;
            try {
                if (paired != null) paired.configurePairedCompanion((javax.net.ssl.HttpsURLConnection)connection);
                else configureTls(context, cleanHost, connection);
                connection.setRequestMethod("POST");
                connection.setUseCaches(false);
                connection.setInstanceFollowRedirects(false);
                connection.setConnectTimeout(PEER_CONNECT_TIMEOUT_MS);
                connection.setReadTimeout(timeout);
                connection.setDoOutput(true);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                connection.setFixedLengthStreamingMode(body.length);
                try (OutputStream output = connection.getOutputStream()) {
                    writing = true;
                    output.write(body);
                }
                int status = connection.getResponseCode();
                if (status == 404) throw new SessionError("unsupported");
                try (java.io.InputStream input = status == 200 ? connection.getInputStream() : connection.getErrorStream();
                     ByteArrayOutputStream bytes = new ByteArrayOutputStream()) {
                    byte[] buffer = new byte[1024]; int count;
                    while (input != null && (count = input.read(buffer)) != -1) {
                        if (bytes.size() + count > MAX_RESPONSE_BYTES) throw new IOException("Invalid screen response");
                        bytes.write(buffer, 0, count);
                    }
                    JSONObject result = new JSONObject(bytes.toString("UTF-8"));
                    if (status != 200) throw new SessionError(result.optString("error"));
                    return result;
                }
            } catch (Exception error) {
                if (writing || network == null || !NetHelper.isNetworkBindingDenied(error)) throw error;
                NetHelper.noteDirectNetworkBindingDenied(network);
                network = null;
            } finally { connection.disconnect(); }
        }
    }

    // Called on the stream shutdown worker after the user confirms a display change.
    static boolean beginSwitch(Context context, String host, String requestId) {
        if (host == null || requestId == null || requestId.isEmpty()) return false;
        try {
            postSession(context, host, "/v1/displays/lease", new JSONObject()
                    .put("requestId", requestId).put("action", "switch"), 1500, null);
            return true;
        } catch (Exception unavailable) {
            return false;
        }
    }

    static void abort(Context context, String host, String requestId) {
        if (host == null || requestId == null || requestId.isEmpty()) return;
        new Thread(() -> {
            try {
                postSession(context, host, "/v1/displays/lease", new JSONObject()
                        .put("requestId", requestId).put("action", "abort"), 5000, null);
            } catch (Exception ignored) { /* The Host's bounded lease still restores on expiry. */ }
        }, "PiuLink display abort").start();
    }

    private static void configureTls(Context context, String host, HttpURLConnection connection) throws Exception {
        com.limelight.computers.ComputerDatabaseManager database = new com.limelight.computers.ComputerDatabaseManager(context);
        try {
            com.limelight.nvstream.http.ComputerDetails matched = null;
            for (com.limelight.nvstream.http.ComputerDetails computer : database.getAllComputers()) {
                for (com.limelight.nvstream.http.ComputerDetails.AddressTuple address : new com.limelight.nvstream.http.ComputerDetails.AddressTuple[] {
                        computer.localAddress, computer.remoteAddress, computer.manualAddress, computer.ipv6Address }) {
                    if (address != null && address.address.equalsIgnoreCase(host)) {
                        if (matched != null && !matched.uuid.equals(computer.uuid)) throw new IOException("Ambiguous Host identity");
                        matched = computer;
                    }
                }
            }
            if (matched == null || matched.serverCert == null) throw new IOException("Pair this Host before controlling displays");
            com.limelight.nvstream.http.NvHTTP paired = new com.limelight.nvstream.http.NvHTTP(
                    new com.limelight.nvstream.http.ComputerDetails.AddressTuple(host, 47989), 47984, "",
                    matched.serverCert, com.limelight.binding.PlatformBinding.getCryptoProvider(context));
            paired.configurePairedCompanion((javax.net.ssl.HttpsURLConnection)connection);
        } finally { database.close(); }
    }
}
