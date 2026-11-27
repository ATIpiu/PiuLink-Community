package com.limelight.piulink;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

import com.limelight.Game;

public final class BackgroundStreamActionReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        if (BackgroundStreamService.ACTION_STOP.equals(intent.getAction())) {
            Game.disconnectBackgroundStream(intent.getStringExtra(BackgroundStreamService.EXTRA_SESSION));
        }
    }
}
