package com.limelight;

import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.ComputerDetails.State;
import com.limelight.nvstream.http.PairingManager.PairState;
import java.util.Arrays;
import java.util.Collections;
import org.junit.Test;
import static org.junit.Assert.*;

public class TvLastSessionTest {
    private final NvApp desktop = new NvApp("Desktop", 3, false);
    private final NvApp game = new NvApp("Game", 8, true);

    @Test public void keepsOneHostOrTheRecordedHostWhenItsStateChanges() {
        assertTrue(TvLastSession.isHomeHost("", "host-a", 1));
        assertFalse(TvLastSession.isHomeHost("", "host-a", 2));
        assertTrue(TvLastSession.isHomeHost("HOST-A", "host-a", 2));
        assertFalse(TvLastSession.isHomeHost("host-a", "host-b", 1));
    }

    @Test public void continueRequiresAnOnlinePairedHostWithItsCertificate() {
        assertEquals(TvLastSession.HomeAction.OFFLINE, TvLastSession.homeAction(State.OFFLINE, true, null));
        assertEquals(TvLastSession.HomeAction.CHECK, TvLastSession.homeAction(State.UNKNOWN, true, null));
        assertEquals(TvLastSession.HomeAction.PAIR, TvLastSession.homeAction(State.ONLINE, false, PairState.PAIRED));
        assertEquals(TvLastSession.HomeAction.PAIR, TvLastSession.homeAction(State.ONLINE, true, PairState.NOT_PAIRED));
        assertEquals(TvLastSession.HomeAction.CONTINUE, TvLastSession.homeAction(State.ONLINE, true, PairState.PAIRED));
    }

    @Test public void continueUsesThePublishedEntryFromThisHost() {
        assertSame(game, TvLastSession.publishedApp(8, Arrays.asList(desktop, game), Collections.emptySet()));
    }

    @Test public void removedOrHiddenAppReturnsToLibrary() {
        assertNull(TvLastSession.publishedApp(8, Collections.singletonList(desktop), Collections.emptySet()));
        assertNull(TvLastSession.publishedApp(8, Arrays.asList(desktop, game), Collections.singleton(8)));
        assertNull(TvLastSession.publishedApp(0, Arrays.asList(desktop, game), Collections.emptySet()));
    }
}
