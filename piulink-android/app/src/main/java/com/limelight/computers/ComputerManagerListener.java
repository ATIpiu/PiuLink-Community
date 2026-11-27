package com.limelight.computers;

import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import java.util.List;

public interface ComputerManagerListener {
    void notifyComputerUpdated(ComputerDetails details);

    default void notifyAppListUpdated(ComputerDetails details, List<NvApp> apps, Integer runningApp) {
        notifyComputerUpdated(details);
    }

    default void notifyAppListUnavailable(ComputerDetails details) { }
}
