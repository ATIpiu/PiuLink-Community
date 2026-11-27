package com.limelight.piulink.ssh;

import org.junit.Test;
import static org.junit.Assert.*;

public class PersistentShellTest {
    @Test public void reconnectAttachesWithoutKillingSession() {
        for (boolean windows : new boolean[] {false}) {
            String command = PersistentShell.command(windows);
            assertTrue(command.contains("tmux new-session -A -s piulink"));
            assertFalse(command.contains("kill-session"));
            assertFalse(command.contains("kill-server"));
        }
        assertEquals("powershell.exe -NoLogo -NoProfile -NoExit", PersistentShell.command(true));
        assertFalse(PersistentShell.command(true).contains("wsl"));
        assertFalse(PersistentShell.command(true).contains("tmux"));
        assertTrue(PersistentShell.command(false).contains("command -v tmux"));
    }
}
