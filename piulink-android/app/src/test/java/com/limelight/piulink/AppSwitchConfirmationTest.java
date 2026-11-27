package com.limelight.piulink;

import com.limelight.nvstream.AppSwitchConfirmation;
import org.junit.Test;
import static org.junit.Assert.*;

public class AppSwitchConfirmationTest {
    @Test public void normalLaunchAndReconnectHaveNoSwitchConsent() {
        assertFalse(new AppSwitchConfirmation(0).consume(7));
    }

    @Test public void onlyTheAppShownAtConfirmationCanBeReplacedOnce() {
        AppSwitchConfirmation confirmation = new AppSwitchConfirmation(7);
        assertTrue(confirmation.consume(7));
        assertFalse(confirmation.consume(7));
    }

    @Test public void changedOrFinishedSessionConsumesStaleConsent() {
        for (int actual : new int[]{0, 9}) {
            AppSwitchConfirmation confirmation = new AppSwitchConfirmation(7);
            assertFalse(confirmation.consume(actual));
            assertFalse(confirmation.consume(7));
        }
    }
}
