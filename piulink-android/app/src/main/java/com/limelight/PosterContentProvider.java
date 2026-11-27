package com.limelight;

import android.content.ContentProvider;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.UriMatcher;
import android.database.Cursor;
import android.net.Uri;
import android.os.ParcelFileDescriptor;

import com.limelight.grid.assets.DiskAssetLoader;

import java.io.File;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.util.List;
import java.util.regex.Pattern;

public class PosterContentProvider extends ContentProvider {


    public static final String AUTHORITY = "poster." + BuildConfig.APPLICATION_ID;
    public static final String PNG_MIME_TYPE = "image/png";
    public static final int APP_ID_PATH_INDEX = 2;
    public static final int COMPUTER_UUID_PATH_INDEX = 1;
    private DiskAssetLoader mDiskAssetLoader;

    private static final UriMatcher sUriMatcher;
    private static final String BOXART_PATH = "boxart";
    private static final int BOXART_URI_ID = 1;
    private static final Pattern SAFE_UUID = Pattern.compile("[A-Za-z0-9._-]{1,128}");

    static {
        sUriMatcher = new UriMatcher(UriMatcher.NO_MATCH);
        sUriMatcher.addURI(AUTHORITY, BOXART_PATH + "/*/#", BOXART_URI_ID);
    }

    @Override
    public ParcelFileDescriptor openFile(Uri uri, String mode) throws FileNotFoundException {
        int match = sUriMatcher.match(uri);
        if (match == BOXART_URI_ID) {
            return openBoxArtFile(uri, mode);
        }
        throw new FileNotFoundException("Unknown poster URI");
    }

    public ParcelFileDescriptor openBoxArtFile(Uri uri, String mode) throws FileNotFoundException {
        if (!"r".equals(mode)) {
            throw new UnsupportedOperationException("This provider is only for read mode");
        }

        List<String> segments = uri.getPathSegments();
        if (segments.size() != 3) {
            throw new FileNotFoundException();
        }
        String appId = segments.get(APP_ID_PATH_INDEX);
        String uuid = segments.get(COMPUTER_UUID_PATH_INDEX);
        if (!SAFE_UUID.matcher(uuid).matches() || ".".equals(uuid) || "..".equals(uuid)) {
            throw new FileNotFoundException("Invalid computer identifier");
        }

        final int numericAppId;
        try {
            numericAppId = Integer.parseInt(appId);
        } catch (NumberFormatException e) {
            throw new FileNotFoundException("Invalid application identifier");
        }
        if (numericAppId < 0) {
            throw new FileNotFoundException("Invalid application identifier");
        }

        try {
            File boxArtRoot = new File(getContext().getCacheDir(), BOXART_PATH).getCanonicalFile();
            File file = mDiskAssetLoader.getFile(uuid, numericAppId).getCanonicalFile();
            String rootPath = boxArtRoot.getPath() + File.separator;
            if (!file.getPath().startsWith(rootPath)) {
                throw new FileNotFoundException("Poster path escapes cache directory");
            }
            if (file.isFile()) {
                return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY);
            }
        } catch (IOException e) {
            throw new FileNotFoundException("Unable to resolve poster path");
        }
        throw new FileNotFoundException();
    }

    @Override
    public int delete(Uri uri, String selection, String[] selectionArgs) {
        throw new UnsupportedOperationException("This provider is only for read mode");
    }

    @Override
    public String getType(Uri uri) {
        return sUriMatcher.match(uri) == BOXART_URI_ID ? PNG_MIME_TYPE : null;
    }

    @Override
    public Uri insert(Uri uri, ContentValues values) {
        throw new UnsupportedOperationException("This provider is only for read mode");
    }

    @Override
    public boolean onCreate() {
        mDiskAssetLoader = new DiskAssetLoader(getContext());
        return true;
    }

    @Override
    public Cursor query(Uri uri, String[] projection, String selection,
                        String[] selectionArgs, String sortOrder) {
        throw new UnsupportedOperationException("This provider doesn't support query");
    }

    @Override
    public int update(Uri uri, ContentValues values, String selection,
                      String[] selectionArgs) {
        throw new UnsupportedOperationException("This provider is support read only");
    }


    public static Uri createBoxArtUri(String uuid, String appId) {
        return new Uri.Builder()
                .scheme(ContentResolver.SCHEME_CONTENT)
                .authority(AUTHORITY)
                .appendPath(BOXART_PATH)
                .appendPath(uuid)
                .appendPath(appId)
                .build();
    }

}
