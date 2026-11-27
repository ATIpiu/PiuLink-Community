package com.limelight.piulink.ssh;

import org.junit.Test;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import static org.junit.Assert.*;

public class BoundedTaskQueueTest {
    @Test public void blockedConnectionHasBoundedBacklogAndDisconnectClearsIt() throws Exception {
        BoundedTaskQueue queue = new BoundedTaskQueue(1);
        CountDownLatch started = new CountDownLatch(1), unblock = new CountDownLatch(1), completed = new CountDownLatch(1);
        AtomicBoolean staleRan = new AtomicBoolean();
        try {
            assertTrue(queue.submit(() -> {
                started.countDown();
                try { unblock.await(); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
            }));
            assertTrue(started.await(3, TimeUnit.SECONDS));
            assertTrue(queue.submit(() -> staleRan.set(true)));
            assertFalse(queue.submit(() -> fail("Rejected work must not execute on the UI thread")));
            queue.clearPending();
            assertTrue(queue.submit(completed::countDown));
            unblock.countDown();
            assertTrue(completed.await(3, TimeUnit.SECONDS));
            assertFalse(staleRan.get());
        } finally { unblock.countDown(); queue.shutdownNow(); }
        assertFalse(queue.submit(() -> fail("Closed queue accepted work")));
    }
}
