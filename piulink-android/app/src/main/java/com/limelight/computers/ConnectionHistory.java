package com.limelight.computers;

import android.content.Context;
import android.content.SharedPreferences;
import com.limelight.nvstream.http.ComputerDetails;
import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Locale;

/** Local route choices. Only an authenticated successful invitation is eligible for automatic reconnect. */
public final class ConnectionHistory {
    public final String uuid, name;
    public final List<Route> routes;

    public static final class Route {
        public final String value, key, label;
        public final long lastUsed;
        Route(String value, JSONObject meta) {
            this.value = value;
            this.key = keyOf(value);
            this.label = meta == null ? "" : meta.optString("label");
            this.lastUsed = meta == null ? 0 : meta.optLong("used");
        }
    }

    private static String keyOf(String value) {
        try {
            byte[] hash = MessageDigest.getInstance("SHA-256").digest(value.getBytes(StandardCharsets.UTF_8));
            return android.util.Base64.encodeToString(hash, android.util.Base64.URL_SAFE | android.util.Base64.NO_PADDING | android.util.Base64.NO_WRAP);
        } catch (NoSuchAlgorithmException impossible) { throw new AssertionError(impossible); }
    }

    private static SharedPreferences meta(Context context) {
        return context.getSharedPreferences("host-route-details", Context.MODE_PRIVATE);
    }

    private static JSONObject details(Context context, String uuid) {
        try { return new JSONObject(meta(context).getString(uuid.toLowerCase(Locale.ROOT), "{}")); }
        catch (JSONException invalid) { return new JSONObject(); }
    }

    static synchronized ConnectionHistory snapshot(Context context, String uuid, String name) {
        JSONObject meta = details(context, uuid);
        List<Route> rows = new ArrayList<>();
        for (String value : read(context, uuid)) {
            JSONObject entry = meta.optJSONObject(keyOf(value));
            if (entry == null || !entry.optBoolean("hidden")) rows.add(new Route(value, entry));
        }
        Collections.sort(rows, (a, b) -> Long.compare(b.lastUsed, a.lastUsed));
        return new ConnectionHistory(uuid, name, rows);
    }

    ConnectionHistory(String uuid, String name, List<Route> routes) {
        this.uuid = uuid;
        this.name = name == null ? uuid : name;
        this.routes = Collections.unmodifiableList(routes);
    }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences("host-route-history", Context.MODE_PRIVATE);
    }

    static String address(ComputerDetails.AddressTuple address) {
        if (address == null) return null;
        String host = address.address;
        if (host.startsWith("127.") || host.equals("::1") || host.equals("0:0:0:0:0:0:0:1") ||
                host.equals("0.0.0.0") || host.equals("::") || host.equalsIgnoreCase("localhost")) return null;
        return address.toString();
    }

    private static List<String> read(Context context, String uuid) {
        List<String> routes = new ArrayList<>();
        try {
            JSONArray saved = new JSONArray(prefs(context).getString(uuid.toLowerCase(Locale.ROOT), "[]"));
            for (int i = 0; i < saved.length(); i++) routes.add(saved.getString(i));
        } catch (JSONException ignored) { }
        return routes;
    }

    static synchronized void remember(Context context, String uuid, String... values) {
        if (uuid == null) return;
        List<String> routes = read(context, uuid);
        JSONObject metadata = details(context, uuid);
        boolean changed = false;
        for (String value : values) {
            if (value == null || routes.contains(value)) continue;
            JSONObject entry = metadata.optJSONObject(keyOf(value));
            if (entry == null || !entry.optBoolean("hidden")) { routes.add(value); changed = true; }
        }
        if (changed) prefs(context).edit().putString(uuid.toLowerCase(Locale.ROOT), new JSONArray(routes).toString()).apply();
    }

    private static void setEntry(Context context, String uuid, String value, String field, Object data) {
        JSONObject metadata = details(context, uuid);
        String key = keyOf(value);
        JSONObject entry = metadata.optJSONObject(key);
        if (entry == null) entry = new JSONObject();
        try {
            entry.put(field, data);
            if (entry.length() == 0) metadata.remove(key);
            else metadata.put(key, entry);
        } catch (JSONException impossible) { throw new IllegalStateException(impossible); }
        meta(context).edit().putString(uuid.toLowerCase(Locale.ROOT), metadata.toString()).apply();
    }

    static synchronized void explicit(Context context, String uuid, String value) {
        if (value == null) return;
        JSONObject entry = details(context, uuid).optJSONObject(keyOf(value));
        if (entry != null && entry.optBoolean("hidden")) setEntry(context, uuid, value, "hidden", null);
        remember(context, uuid, value);
    }

    static synchronized boolean used(Context context, String uuid, String value) {
        if (value == null || !read(context, uuid).contains(value)) return false;
        JSONObject entry = details(context, uuid).optJSONObject(keyOf(value));
        if (entry != null && entry.optBoolean("hidden")) return false;
        setEntry(context, uuid, value, "used", System.currentTimeMillis());
        return true;
    }

    static synchronized void rename(Context context, String uuid, String value, String label) {
        if (read(context, uuid).contains(value))
            setEntry(context, uuid, value, "label", label.isEmpty() ? null : label);
    }

    static synchronized void remove(Context context, String uuid, String value) {
        List<String> routes = read(context, uuid);
        if (!routes.remove(value)) return;
        // Remember only the fingerprint of a removed endpoint so discovery cannot recreate the row.
        JSONObject metadata = details(context, uuid);
        try { metadata.put(keyOf(value), new JSONObject().put("hidden", true)); }
        catch (JSONException impossible) { throw new IllegalStateException(impossible); }
        meta(context).edit().putString(uuid.toLowerCase(Locale.ROOT), metadata.toString()).apply();
        prefs(context).edit().putString(uuid.toLowerCase(Locale.ROOT), new JSONArray(routes).toString()).apply();
    }

    static synchronized void forget(Context context, String uuid) {
        prefs(context).edit().remove(uuid.toLowerCase(Locale.ROOT)).apply();
        meta(context).edit().remove(uuid.toLowerCase(Locale.ROOT)).apply();
    }

    static List<String> hostIds(Context context) {
        return new ArrayList<>(prefs(context).getAll().keySet());
    }
}
