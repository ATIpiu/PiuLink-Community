package com.limelight.computers;

import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.ComputerDetails.AddressTuple;
import org.junit.Test;
import java.io.IOException;
import java.util.Arrays;
import java.util.Collections;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import static org.junit.Assert.*;

public class DesktopRouteSelectorTest {
    private final AddressTuple known = new AddressTuple("192.0.2.1", 47989);
    private final ComputerDetails paired = new ComputerDetails();

    @Test public void fastDirectRouteSkipsSignaling() throws Exception {
        AtomicBoolean contacted = new AtomicBoolean();
        assertSame(paired, DesktopRouteSelector.firstVerified(Collections.singletonList(known), a -> paired,
                () -> { contacted.set(true); return null; }, 4000, 5000));
        assertFalse(contacted.get());
    }

    @Test public void vpnPrefersVerifiedLanWhenBothMethodsRespondPromptly() throws Exception {
        AddressTuple alternate = new AddressTuple("100.64.0.2", 47989);
        ComputerDetails overlay = new ComputerDetails();
        overlay.activeAddress = alternate;
        paired.activeAddress = known;
        CountDownLatch overlayChecked = new CountDownLatch(1);
        assertSame(paired, DesktopRouteSelector.firstVerified(Arrays.asList(known, alternate),
                Collections.singletonList(known), address -> {
                    if (address.equals(alternate)) { overlayChecked.countDown(); return overlay; }
                    try { return overlayChecked.await(1, TimeUnit.SECONDS) ? paired : null; }
                    catch (InterruptedException e) { Thread.currentThread().interrupt(); return null; }
                }, null, 750, 1500));
    }

    @Test public void vpnUsesVerifiedAlternateWhenLanIsSlow() throws Exception {
        AddressTuple alternate = new AddressTuple("100.64.0.2", 47989);
        paired.activeAddress = alternate;
        assertSame(paired, DesktopRouteSelector.firstVerified(Arrays.asList(known, alternate),
                Collections.singletonList(known), address -> {
                    if (address.equals(alternate)) return paired;
                    try { Thread.sleep(2000); } catch (InterruptedException e) { Thread.currentThread().interrupt(); }
                    return null;
                }, null, 750, 1000));
    }

    @Test public void rejectedAddressesStartInvitationWithoutWaitingForHeadStart() throws Exception {
        assertSame(paired, DesktopRouteSelector.firstVerified(Collections.singletonList(known), a -> null,
                () -> paired, 10000, 1500));
    }

    @Test public void slowAddressDoesNotBlockPunchAndLosingWorkerIsInterrupted() throws Exception {
        CountDownLatch cancelled = new CountDownLatch(1);
        CountDownLatch started = new CountDownLatch(1);
        assertSame(paired, DesktopRouteSelector.firstVerified(Collections.singletonList(known), a -> {
            started.countDown();
            try { Thread.sleep(10000); }
            catch (InterruptedException done) { cancelled.countDown(); Thread.currentThread().interrupt(); }
            return null;
        }, () -> started.await(1, TimeUnit.SECONDS) ? paired : null, 10, 2000));
        assertTrue(cancelled.await(1, TimeUnit.SECONDS));
    }

    @Test public void signalingFailureStillAllowsSlowerVerifiedAddress() throws Exception {
        CountDownLatch signaling = new CountDownLatch(1);
        assertSame(paired, DesktopRouteSelector.firstVerified(Arrays.asList(known, new AddressTuple("192.0.2.2", 47989)),
                a -> {
                    if (a.equals(known)) return null;
                    try { return signaling.await(1, TimeUnit.SECONDS) ? paired : null; }
                    catch (InterruptedException e) { Thread.currentThread().interrupt(); return null; }
                }, () -> { signaling.countDown(); throw new IOException(); }, 10, 2000));
    }
}
