package com.limelight.piulink;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

import com.limelight.R;
import com.limelight.Game;

public final class BackgroundStreamService extends Service {
    public static final String ACTION_STOP = "labs.atipiu.piulink.STOP_BACKGROUND_STREAM";
    public static final String EXTRA_SESSION = "backgroundSession";
    public static final String EXTRA_TIMEOUT_MINUTES = "timeoutMinutes";
    private static final String CHANNEL_ID = "piulink_background_stream";
    private static final int NOTIFICATION_ID = 48014; // 48012 belongs to FileReceiveService.
    private final Handler handler = new Handler(Looper.getMainLooper());
    private Runnable timeout;

    @Override public void onCreate() {
        super.onCreate();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(CHANNEL_ID,
                    getString(R.string.background_stream_channel), NotificationManager.IMPORTANCE_LOW);
            channel.setDescription(getString(R.string.background_stream_channel_summary));
            getSystemService(NotificationManager.class).createNotificationChannel(channel);
        }
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        String session = intent == null ? null : intent.getStringExtra(EXTRA_SESSION);
        if (session == null) { stopSelfResult(startId); return START_NOT_STICKY; }
        int minutes = intent.getIntExtra(EXTRA_TIMEOUT_MINUTES, 15);
        Intent stop = new Intent(this, BackgroundStreamActionReceiver.class).setAction(ACTION_STOP)
                .setData(new android.net.Uri.Builder().scheme("piulink").authority("background").appendPath(session).build())
                .putExtra(EXTRA_SESSION, session);
        PendingIntent stopAction = PendingIntent.getBroadcast(this, 0, stop,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Intent resume = new Intent(this, Game.class)
                .addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent resumeAction = PendingIntent.getActivity(this, 1, resume,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Notification.Builder builder = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                ? new Notification.Builder(this, CHANNEL_ID) : new Notification.Builder(this);
        builder.setSmallIcon(R.mipmap.ic_launcher)
                .setContentTitle(getString(R.string.background_stream_active))
                .setContentText(minutes <= 0
                        ? getString(R.string.background_stream_no_timeout)
                        : getString(R.string.background_stream_timeout, minutes))
                .setOngoing(true)
                .setCategory(Notification.CATEGORY_SERVICE)
                .setContentIntent(resumeAction)
                .addAction(0, getString(R.string.stream_disconnect), stopAction);
        startForeground(NOTIFICATION_ID, builder.build());
        if (timeout != null) handler.removeCallbacks(timeout);
        timeout = () -> {
            sendBroadcast(stop);
            stopSelfResult(startId);
        };
        if (minutes > 0) handler.postDelayed(timeout, minutes * 60_000L);
        return START_NOT_STICKY;
    }

    @Override public void onDestroy() {
        if (timeout != null) handler.removeCallbacks(timeout);
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
