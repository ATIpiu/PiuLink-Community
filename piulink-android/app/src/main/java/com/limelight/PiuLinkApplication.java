package com.limelight;

import android.app.Activity;
import android.app.Application;

import java.util.HashSet;
import java.util.Set;

public final class PiuLinkApplication extends Application {
    // Both activity lifecycle events and session cleanup notifications run on the main thread.
    private final Set<AppView> resumedAppLists = new HashSet<>();

    void refreshRunningAppForHost(String hostUuid) {
        for (AppView appList : resumedAppLists) {
            appList.refreshRunningAppForHost(hostUuid);
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        DiagnosticReport.install(this);
        LimeLog.initialize(this);
        registerActivityLifecycleCallbacks(new ActivityLifecycleCallbacks() {
            public void onActivityResumed(Activity activity) {
                if (activity instanceof AppView) resumedAppLists.add((AppView) activity);
                com.limelight.piulink.transfer.FileReceiveService.start(activity);
            }
            public void onActivityCreated(android.app.Activity a, android.os.Bundle b) { }
            public void onActivityStarted(android.app.Activity a) { }
            public void onActivityPaused(Activity activity) {
                resumedAppLists.remove(activity);
            }
            public void onActivityStopped(android.app.Activity a) { }
            public void onActivitySaveInstanceState(android.app.Activity a, android.os.Bundle b) { }
            public void onActivityDestroyed(android.app.Activity a) { }
        });
    }
}
