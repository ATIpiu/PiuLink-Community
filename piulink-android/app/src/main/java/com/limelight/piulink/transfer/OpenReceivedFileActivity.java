package com.limelight.piulink.transfer;

import android.app.Activity;
import android.app.DownloadManager;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.widget.Toast;
import com.limelight.R;
import java.io.IOException;

public final class OpenReceivedFileActivity extends Activity {
    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        Uri file = getIntent().getData();
        try {
            if (state != null || file == null || !"content".equals(file.getScheme())) return;
            try (ParcelFileDescriptor descriptor = getContentResolver().openFileDescriptor(file, "r")) {
                if (descriptor == null) throw new IOException("Missing file");
            }
            Intent view = new Intent(Intent.ACTION_VIEW).setDataAndType(file, getIntent().getType())
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(view);
        } catch (ActivityNotFoundException missingViewer) {
            Toast.makeText(this, R.string.received_file_no_viewer, Toast.LENGTH_LONG).show();
            try { startActivity(new Intent(DownloadManager.ACTION_VIEW_DOWNLOADS)); }
            catch (ActivityNotFoundException ignored) { }
        } catch (IOException | SecurityException unavailable) {
            Toast.makeText(this, R.string.received_file_unavailable, Toast.LENGTH_LONG).show();
        } finally {
            finish();
        }
    }
}
