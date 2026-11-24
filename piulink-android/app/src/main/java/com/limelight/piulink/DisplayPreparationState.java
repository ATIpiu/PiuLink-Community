package com.limelight.piulink;

import java.util.UUID;
import java.util.WeakHashMap;

/** A single display preparation. Its ID travels with the stream that owns it. */
final class DisplayPreparationState {
    final String host;
    final String requestId = UUID.randomUUID().toString();
    private DisplayPreparationState(String host) { this.host = host; }

    static final class Pending<T> {
        private final WeakHashMap<T, DisplayPreparationState> requests = new WeakHashMap<>();

        DisplayPreparationState start(T owner, String host) {
            DisplayPreparationState request = new DisplayPreparationState(host);
            requests.put(owner, request);
            return request;
        }

        DisplayPreparationState cancel(T owner) { return requests.remove(owner); }

        boolean handOff(T owner, DisplayPreparationState request) {
            if (requests.get(owner) != request) return false;
            requests.remove(owner);
            return true;
        }
    }
}
