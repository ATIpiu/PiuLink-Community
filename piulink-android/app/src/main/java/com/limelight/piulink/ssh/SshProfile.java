package com.limelight.piulink.ssh;

import android.content.Context;
import org.json.JSONArray;
import org.json.JSONObject;
import java.util.ArrayList;
import java.util.List;

/** Connection metadata only. Passwords and private keys are never saved here. */
public final class SshProfile {
    public final String host, user;
    public final int port;
    public SshProfile(String host, String user, int port) {
        if (host == null || user == null) throw new IllegalArgumentException("Missing SSH address or user");
        host = host.trim(); user = user.trim();
        if (host.isEmpty() || invalidCharacters(host, true) || user.isEmpty() ||
                invalidCharacters(user, false) || port < 1 || port > 65535)
            throw new IllegalArgumentException("Invalid SSH address, user or port");
        this.host = host; this.user = user; this.port = port;
    }
    public String label() { return user + "@" + host + ":" + port; }
    private static boolean invalidCharacters(String value, boolean hostname) {
        for (int i = 0; i < value.length(); i++) {
            char character = value.charAt(i);
            if (Character.isISOControl(character) ||
                    (hostname && (Character.isWhitespace(character) || character == '/' || character == '\\')))
                return true;
        }
        return false;
    }
    public static List<SshProfile> load(Context context) {
        try {
            return decode(context.getSharedPreferences("ssh_profiles", 0).getString("profiles", "[]"));
        } catch (Exception e) { throw new IllegalStateException("Unable to read SSH profiles", e); }
    }
    static List<SshProfile> decode(String json) {
        List<SshProfile> profiles = new ArrayList<>();
        try {
            JSONArray list = new JSONArray(json);
            for (int i = 0; i < list.length(); i++) {
                JSONObject item = list.getJSONObject(i);
                if (!(item.get("host") instanceof String) || !(item.get("user") instanceof String) ||
                        !(item.get("port") instanceof Number) ||
                        ((Number) item.get("port")).doubleValue() != item.getInt("port"))
                    throw new IllegalArgumentException("Invalid SSH profile field type");
                profiles.add(new SshProfile(item.getString("host"), item.getString("user"), item.getInt("port")));
            }
        } catch (Exception e) { throw new IllegalStateException("Unable to read SSH profiles", e); }
        return profiles;
    }
    public void save(Context context) {
        replace(context, null);
    }
    public void replace(Context context, SshProfile previous) {
        List<SshProfile> profiles = load(context);
        if (previous != null) previous.removeMatching(profiles);
        removeMatching(profiles);
        profiles.add(0, this);
        write(context, profiles);
    }
    public void remove(Context context) {
        List<SshProfile> profiles = load(context);
        removeMatching(profiles);
        write(context, profiles);
    }
    private void removeMatching(List<SshProfile> profiles) {
        java.util.Iterator<SshProfile> iterator = profiles.iterator();
        while (iterator.hasNext()) if (iterator.next().label().equals(label())) iterator.remove();
    }
    private static void write(Context context, List<SshProfile> profiles) {
        JSONArray list = new JSONArray();
        try {
            for (SshProfile p : profiles) {
                JSONObject item = new JSONObject();
                item.put("host", p.host); item.put("user", p.user); item.put("port", p.port);
                list.put(item);
            }
        } catch (Exception e) { throw new IllegalStateException(e); }
        context.getSharedPreferences("ssh_profiles", 0).edit().putString("profiles", list.toString()).apply();
    }
}
