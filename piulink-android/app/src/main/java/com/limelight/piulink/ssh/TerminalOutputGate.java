package com.limelight.piulink.ssh;

import java.util.concurrent.CountDownLatch;

/** A render acknowledgement belongs to one frame of one SSH terminal attachment. */
final class TerminalOutputGate {
    static final class Frame {
        final int id;
        final CountDownLatch rendered = new CountDownLatch(1);
        Frame(int id) { this.id = id; }
    }
    private int stream, nextId;
    private Frame pending;

    synchronized void reset(int stream) {
        this.stream = stream;
        if (pending != null) pending.rendered.countDown();
        pending = null;
    }
    synchronized Frame begin(int stream) {
        if (this.stream != stream) return null;
        if (pending != null) throw new IllegalStateException("Output frame is still pending");
        pending = new Frame(++nextId);
        return pending;
    }
    synchronized void acknowledge(int id) {
        if (pending != null && pending.id == id) {
            pending.rendered.countDown();
            pending = null;
        }
    }
}
