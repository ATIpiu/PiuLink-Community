package com.limelight.nvstream;

/** A launch can replace only the application named when the user confirmed the switch. */
public final class AppSwitchConfirmation {
    private int confirmedId;

    public AppSwitchConfirmation(int confirmedId) {
        this.confirmedId = confirmedId;
    }

    public synchronized boolean consume(int runningId) {
        int expected = confirmedId;
        confirmedId = 0;
        return runningId > 0 && runningId == expected;
    }
}
