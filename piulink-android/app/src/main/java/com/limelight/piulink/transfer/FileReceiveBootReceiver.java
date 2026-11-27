package com.limelight.piulink.transfer;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Restores the approved file receiver after a reboot or an app upgrade. */
public final class FileReceiveBootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        FileReceiveService.start(context.getApplicationContext());
    }
}
