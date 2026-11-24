package com.limelight.piulink.ssh;

import org.junit.Test;
import static org.junit.Assert.*;

public class TerminalOutputGateTest {
    @Test public void oldAcknowledgementCannotReleaseNewSession() {
        TerminalOutputGate gate = new TerminalOutputGate();
        gate.reset(1);
        TerminalOutputGate.Frame old = gate.begin(1);
        gate.reset(2);
        assertEquals(0, old.rendered.getCount());
        TerminalOutputGate.Frame current = gate.begin(2);
        gate.acknowledge(old.id);
        assertEquals(1, current.rendered.getCount());
        assertNull(gate.begin(1));
        gate.acknowledge(current.id);
        assertEquals(0, current.rendered.getCount());
        assertNotNull(gate.begin(2));
    }
    @Test public void duplicateAcknowledgementCannotReleaseNextFrame() {
        TerminalOutputGate gate = new TerminalOutputGate();
        gate.reset(1);
        TerminalOutputGate.Frame first = gate.begin(1);
        gate.acknowledge(first.id);
        TerminalOutputGate.Frame second = gate.begin(1);
        gate.acknowledge(first.id);
        assertEquals(1, second.rendered.getCount());
        gate.reset(2);
        assertEquals(0, second.rendered.getCount());
    }
}
