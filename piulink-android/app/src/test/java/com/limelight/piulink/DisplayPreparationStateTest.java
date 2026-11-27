package com.limelight.piulink;

import org.junit.Test;
import static org.junit.Assert.*;

public class DisplayPreparationStateTest {
    @Test public void leavingThePickerCannotCancelItsHandedOffStream() {
        DisplayPreparationState.Pending<Object> pending = new DisplayPreparationState.Pending<>();
        Object picker = new Object(), game = new Object();
        DisplayPreparationState first = pending.start(picker, "same-host");
        assertTrue(pending.handOff(picker, first));
        DisplayPreparationState next = pending.start(game, first.host);
        assertNull(pending.cancel(picker));
        assertNotEquals(first.requestId, next.requestId);
        assertSame(next, pending.cancel(game));
    }

    @Test public void staleCompletionCannotReleaseANewerRequestOnTheSamePage() {
        DisplayPreparationState.Pending<Object> pending = new DisplayPreparationState.Pending<>();
        Object page = new Object();
        DisplayPreparationState old = pending.start(page, "same-host");
        assertSame(old, pending.cancel(page));
        DisplayPreparationState next = pending.start(page, old.host);
        assertFalse(pending.handOff(page, old));
        assertTrue(pending.handOff(page, next));
        assertNull(pending.cancel(page));
    }
}
