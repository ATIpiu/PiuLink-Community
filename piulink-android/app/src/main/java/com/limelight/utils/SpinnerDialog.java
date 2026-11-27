package com.limelight.utils;

import java.util.ArrayList;
import java.util.Iterator;

import android.app.Activity;
import android.app.ProgressDialog;
import android.content.DialogInterface;
import android.content.DialogInterface.OnCancelListener;

public class SpinnerDialog implements Runnable,OnCancelListener {
    private final int title;
    private final String message;
    private final Activity activity;
    private ProgressDialog progress;
    private final boolean finish;

    private static final ArrayList<SpinnerDialog> rundownDialogs = new ArrayList<>();

    private SpinnerDialog(Activity activity, int title, String message, boolean finish)
    {
        this.activity = activity;
        this.title = title;
        this.message = message;
        this.progress = null;
        this.finish = finish;
    }

    public static SpinnerDialog displayDialog(Activity activity, int title, String message, boolean finish)
    {
        SpinnerDialog spinner = new SpinnerDialog(activity, title, message, finish);
        activity.runOnUiThread(spinner);
        return spinner;
    }

    public static void closeDialogs(Activity activity)
    {
        synchronized (rundownDialogs) {
            Iterator<SpinnerDialog> i = rundownDialogs.iterator();
            while (i.hasNext()) {
                SpinnerDialog dialog = i.next();
                if (dialog.activity == activity) {
                    i.remove();
                    if (dialog.progress.isShowing()) {
                        dialog.progress.dismiss();
                    }
                }
            }
        }
    }

    public void dismiss()
    {
        // Running again with progress != null will destroy it
        activity.runOnUiThread(this);
    }

    public void setMessage(final String message)
    {
        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (progress != null && progress.isShowing()) progress.setMessage(message);
            }
        });
    }

    @Override
    public void run() {

        // If we're dying, don't bother doing anything
        if (activity.isFinishing()) {
            return;
        }

        if (progress == null)
        {
            progress = new ProgressDialog(activity);

            progress.setTitle(title);
            progress.setMessage(message);
            progress.setProgressStyle(ProgressDialog.STYLE_SPINNER);
            progress.setOnCancelListener(this);

            progress.setCancelable(true);
            progress.setCanceledOnTouchOutside(false);


            synchronized (rundownDialogs) {
                if (ReminderPreferences.showProgress(activity, progress, activity.getText(title), title))
                    rundownDialogs.add(this);
            }
        }
        else
        {
            synchronized (rundownDialogs) {
                if (rundownDialogs.remove(this) && progress.isShowing()) {
                    progress.dismiss();
                }
            }
        }
    }

    @Override
    public void onCancel(DialogInterface dialog) {
        synchronized (rundownDialogs) {
            rundownDialogs.remove(this);
        }

        if (finish) activity.finish();
    }
}
