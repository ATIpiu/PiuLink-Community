package com.limelight.computers;

import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.ComputerDetails.AddressTuple;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.Callable;
import java.util.concurrent.ConcurrentLinkedQueue;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.ExecutorCompletionService;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.function.Function;

final class DesktopRouteSelector {
    private DesktopRouteSelector() {}

    // Each probe returns only a certificate-verified paired Host. Until a winner is
    // accepted by the foreground activity, probing does not change the selected route.
    static ComputerDetails firstVerified(List<AddressTuple> addresses, Function<AddressTuple, ComputerDetails> verify,
                                         Callable<ComputerDetails> invite, long headStartMs, long timeoutMs)
            throws InterruptedException {
        return firstVerified(addresses, Collections.emptyList(), verify, invite, headStartMs, timeoutMs);
    }

    static ComputerDetails firstVerified(List<AddressTuple> addresses, List<AddressTuple> preferredLan,
                                         Function<AddressTuple, ComputerDetails> verify, Callable<ComputerDetails> invite,
                                         long headStartMs, long timeoutMs) throws InterruptedException {
        ConcurrentLinkedQueue<AddressTuple> pending = new ConcurrentLinkedQueue<>(addresses);
        int remaining = Math.min(4, pending.size());
        ExecutorService workers = Executors.newFixedThreadPool(remaining + 1);
        ExecutorCompletionService<ComputerDetails> completed = new ExecutorCompletionService<>(workers);
        long start = System.nanoTime();
        long punchAt = start + TimeUnit.MILLISECONDS.toNanos(Math.min(headStartMs, timeoutMs));
        long deadline = start + TimeUnit.MILLISECONDS.toNanos(timeoutMs);
        // Under a VPN, the first authenticated reply may come from an overlay even on
        // home Wi-Fi. Keep that reply ready while the on-link route finishes its handshake.
        long lanDeadline = start + TimeUnit.MILLISECONDS.toNanos(150);
        ComputerDetails alternate = null;
        try {
            for (int i = 0; i < remaining; i++) completed.submit(() -> {
                AddressTuple address;
                while (!Thread.currentThread().isInterrupted() && (address = pending.poll()) != null) {
                    ComputerDetails peer = verify.apply(address);
                    if (peer != null) return peer;
                }
                return null;
            });
            while (remaining > 0 || invite != null) {
                long now = System.nanoTime();
                if (now >= deadline || (alternate != null && (remaining == 0 || now >= lanDeadline)))
                    return alternate;
                if (invite != null && (remaining == 0 || now >= punchAt)) {
                    completed.submit(invite);
                    invite = null;
                    remaining++;
                }
                long waitUntil = invite == null ? deadline : punchAt;
                if (alternate != null) waitUntil = Math.min(waitUntil, lanDeadline);
                Future<ComputerDetails> reply = completed.poll(waitUntil - now, TimeUnit.NANOSECONDS);
                if (reply == null) continue;
                remaining--;
                try {
                    ComputerDetails peer = reply.get();
                    if (peer != null) {
                        if (preferredLan.isEmpty() || preferredLan.contains(peer.activeAddress)) return peer;
                        if (alternate == null) alternate = peer;
                    }
                } catch (ExecutionException unavailable) {
                    // Other methods of reaching the same Host may still succeed.
                }
            }
            return alternate;
        } finally {
            workers.shutdownNow();
        }
    }
}
