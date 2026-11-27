package com.limelight.piulink.ssh;

import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** Never runs blocking network writes on the caller and never grows without a bound. */
final class BoundedTaskQueue {
    private final ThreadPoolExecutor executor;
    BoundedTaskQueue(int capacity) {
        executor = new ThreadPoolExecutor(1, 1, 0, TimeUnit.MILLISECONDS,
                new ArrayBlockingQueue<>(capacity));
    }
    boolean submit(Runnable task) {
        try { executor.execute(task); return true; }
        catch (RejectedExecutionException e) { return false; }
    }
    void clearPending() { executor.getQueue().clear(); }
    void shutdownNow() { executor.shutdownNow(); }
}
