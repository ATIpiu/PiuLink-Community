package com.limelight.grid;

import android.content.Context;
import android.graphics.BitmapFactory;
import android.view.View;
import android.widget.ImageView;
import android.widget.ProgressBar;
import android.widget.TextView;

import com.limelight.AppView;
import com.limelight.LimeLog;
import com.limelight.R;
import com.limelight.grid.assets.CachedAppAssetLoader;
import com.limelight.grid.assets.DiskAssetLoader;
import com.limelight.grid.assets.NetworkAssetLoader;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.preferences.PreferenceConfiguration;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

@SuppressWarnings("unchecked")
public class AppGridAdapter extends GenericGridAdapter<AppView.AppObject> {
    public static final int FILTER_ALL = 0;
    public static final int FILTER_FAVORITES = 1;
    public static final int FILTER_RECENT = 2;

    private static final int ART_WIDTH_PX = 300;
    private static final int SMALL_WIDTH_DP = 100;
    private static final int LARGE_WIDTH_DP = 150;

    private final ComputerDetails computer;
    private final String uniqueId;
    private final boolean showHiddenApps;

    private CachedAppAssetLoader loader;
    private Set<Integer> hiddenAppIds = new HashSet<>();
    private Set<Integer> favoriteAppIds = new HashSet<>();
    private List<Integer> recentAppIds = new ArrayList<>();
    private ArrayList<AppView.AppObject> allApps = new ArrayList<>();
    private String searchQuery = "";
    private int filterMode = FILTER_ALL;

    public AppGridAdapter(Context context, PreferenceConfiguration prefs, ComputerDetails computer, String uniqueId, boolean showHiddenApps) {
        super(context, getLayoutIdForPreferences(prefs));

        this.computer = computer;
        this.uniqueId = uniqueId;
        this.showHiddenApps = showHiddenApps;

        updateLayoutWithPreferences(context, prefs);
    }

    public void updateHiddenApps(Set<Integer> newHiddenAppIds, boolean hideImmediately) {
        this.hiddenAppIds.clear();
        this.hiddenAppIds.addAll(newHiddenAppIds);

        if (hideImmediately) {
            rebuildVisibleApps(true);
        }
        else {
            // Just update the isHidden state to show the correct UI indication
            for (AppView.AppObject app : allApps) {
                app.isHidden = hiddenAppIds.contains(app.app.getAppId());
            }
            notifyDataSetChanged();
        }
    }

    public void updateLibraryState(Set<Integer> newFavoriteAppIds,
                                   List<Integer> newRecentAppIds,
                                   int newFilterMode, String newSearchQuery) {
        favoriteAppIds.clear();
        favoriteAppIds.addAll(newFavoriteAppIds);
        recentAppIds.clear();
        recentAppIds.addAll(newRecentAppIds);
        filterMode = newFilterMode;
        searchQuery = normalizeQuery(newSearchQuery);
        rebuildVisibleApps(true);
    }

    public void updateFavoriteApps(Set<Integer> newFavoriteAppIds) {
        favoriteAppIds.clear();
        favoriteAppIds.addAll(newFavoriteAppIds);
        rebuildVisibleApps(true);
    }

    public void updateRecentApps(List<Integer> newRecentAppIds) {
        recentAppIds.clear();
        recentAppIds.addAll(newRecentAppIds);
        rebuildVisibleApps(true);
    }

    public void setFilterMode(int newFilterMode) {
        filterMode = newFilterMode;
        rebuildVisibleApps(true);
    }

    public void setSearchQuery(String newSearchQuery) {
        searchQuery = normalizeQuery(newSearchQuery);
        rebuildVisibleApps(true);
    }

    public void refreshLibrary() {
        sortAlphabetically(allApps);
        rebuildVisibleApps(true);
    }

    public AppView.AppObject findAppById(int appId) {
        for (AppView.AppObject app : allApps) {
            if (app.app.getAppId() == appId) {
                return app;
            }
        }
        return null;
    }

    public List<AppView.AppObject> getAllAppsSnapshot() {
        return new ArrayList<>(allApps);
    }

    public boolean updateRunningApp(int runningAppId) {
        boolean updated = false;
        for (AppView.AppObject app : allApps) {
            boolean isRunning = app.app.getAppId() == runningAppId;
            if (app.isRunning != isRunning) {
                app.isRunning = isRunning;
                updated = true;
            }
        }
        if (updated) {
            rebuildVisibleApps(true);
        }
        return updated;
    }

    private static String normalizeQuery(String query) {
        return query == null ? "" : query.trim().toLowerCase(Locale.getDefault());
    }

    private void rebuildVisibleApps(boolean notify) {
        itemList.clear();
        for (AppView.AppObject app : allApps) {
            int appId = app.app.getAppId();
            app.isHidden = hiddenAppIds.contains(appId);
            app.isFavorite = false;
            app.recentRank = recentAppIds.indexOf(appId);

            if (!showHiddenApps && app.isHidden) {
                continue;
            }
            if (!searchQuery.isEmpty() &&
                    !app.app.getAppName().toLowerCase(Locale.getDefault()).contains(searchQuery)) {
                continue;
            }
            if (filterMode == FILTER_FAVORITES && !app.isFavorite) {
                continue;
            }
            if (filterMode == FILTER_RECENT && app.recentRank < 0) {
                continue;
            }
            itemList.add(app);
        }

        sortVisibleList(itemList);
        if (notify) {
            notifyDataSetChanged();
        }
    }

    private static int getLayoutIdForPreferences(PreferenceConfiguration prefs) {
        if (prefs.smallIconMode) {
            return R.layout.app_grid_item_small;
        }
        else {
            return R.layout.app_grid_item;
        }
    }

    public void updateLayoutWithPreferences(Context context, PreferenceConfiguration prefs) {
        int dpi = context.getResources().getDisplayMetrics().densityDpi;
        int dp;

        if (prefs.smallIconMode) {
            dp = SMALL_WIDTH_DP;
        }
        else {
            dp = LARGE_WIDTH_DP;
        }

        double scalingDivisor = ART_WIDTH_PX / (dp * (dpi / 160.0));
        if (scalingDivisor < 1.0) {
            // We don't want to make them bigger before draw-time
            scalingDivisor = 1.0;
        }
        LimeLog.info("Art scaling divisor: " + scalingDivisor);

        if (loader != null) {
            loader.close();
        }

        this.loader = new CachedAppAssetLoader(computer, scalingDivisor,
                new NetworkAssetLoader(context, uniqueId),
                new DiskAssetLoader(context),
                BitmapFactory.decodeResource(context.getResources(), R.drawable.no_app_image));

        // This will trigger the view to reload with the new layout
        setLayoutId(getLayoutIdForPreferences(prefs));
    }

    public void close() {
        loader.close();
    }

    public void cancelQueuedOperations() {
        loader.cancelForegroundLoads();
        loader.cancelBackgroundLoads();
        loader.freeCacheMemory();
    }

    private static void sortAlphabetically(List<AppView.AppObject> list) {
        Collections.sort(list, new Comparator<AppView.AppObject>() {
            @Override
            public int compare(AppView.AppObject lhs, AppView.AppObject rhs) {
                return lhs.app.getAppName().compareToIgnoreCase(rhs.app.getAppName());
            }
        });
    }

    private static void sortVisibleList(List<AppView.AppObject> list) {
        Collections.sort(list, new Comparator<AppView.AppObject>() {
            @Override
            public int compare(AppView.AppObject lhs, AppView.AppObject rhs) {
                if (lhs.recentRank >= 0 || rhs.recentRank >= 0) {
                    if (lhs.recentRank < 0) {
                        return 1;
                    }
                    if (rhs.recentRank < 0) {
                        return -1;
                    }
                    if (lhs.recentRank != rhs.recentRank) {
                        return lhs.recentRank - rhs.recentRank;
                    }
                }
                return lhs.app.getAppName().compareToIgnoreCase(rhs.app.getAppName());
            }
        });
    }

    public void addApp(AppView.AppObject app) {
        // Update hidden state
        app.isHidden = hiddenAppIds.contains(app.app.getAppId());
        app.isFavorite = false;
        app.recentRank = recentAppIds.indexOf(app.app.getAppId());

        // Always add the app to the all apps list
        allApps.add(app);
        sortAlphabetically(allApps);

        // Queue a request to fetch this bitmap into cache and rebuild the active view.
        loader.queueCacheLoad(app.app);
        rebuildVisibleApps(false);
    }

    public void removeApp(AppView.AppObject app) {
        itemList.remove(app);
        allApps.remove(app);
        rebuildVisibleApps(false);
    }

    @Override
    public void clear() {
        super.clear();
        allApps.clear();
    }

    @Override
    public void populateView(View parentView, ImageView imgView, ProgressBar prgView, TextView txtView, ImageView overlayView, AppView.AppObject obj) {
        // Let the cached asset loader handle it
        imgView.setClipToOutline(true);
        loader.populateImageView(obj.app, imgView, txtView);
        ImageView lunarArt = parentView.findViewById(R.id.lunarDesktopArt);
        if (lunarArt != null) {
            boolean desktop = obj.app.hasDesktopName();
            boolean terminal = obj.app.getAppId() == com.limelight.piulink.ssh.ComputerSsh.APP_ID;
            lunarArt.setVisibility(View.VISIBLE);
            lunarArt.setImageResource(desktop ? R.drawable.lunar_horizon : R.drawable.library_orbits);
            android.widget.FrameLayout.LayoutParams art = (android.widget.FrameLayout.LayoutParams) imgView.getLayoutParams();
            float density = context.getResources().getDisplayMetrics().density;
            int width = Math.round((terminal ? 145 : desktop ? 44 : 104) * density);
            if (art.width != width) {
                art.width = width;
                art.height = Math.round((terminal ? 90 : desktop ? 55 : 112) * density);
                art.gravity = android.view.Gravity.END | android.view.Gravity.TOP;
                art.setMarginEnd(Math.round(16 * density));
                art.topMargin = Math.round(12 * density);
                imgView.setLayoutParams(art);
            }
            imgView.setScaleType(ImageView.ScaleType.FIT_CENTER);
            if (terminal) imgView.setImageResource(R.drawable.library_terminal);
            if (desktop) txtView.setText(R.string.lunar_desktop);
        }
        parentView.setContentDescription(obj.app.getAppName() +
                (obj.isRunning ? ", " + context.getString(R.string.app_running) : "") +
                (obj.isFavorite ? ", " + context.getString(R.string.app_favorite) : ""));

        ImageView favoriteView = parentView.findViewById(R.id.grid_favorite);
        if (favoriteView != null) {
            favoriteView.setVisibility(obj.isFavorite ? View.VISIBLE : View.GONE);
        }

        if (obj.isRunning) {
            // Show the play button overlay
            overlayView.setImageResource(R.drawable.ic_play);
            overlayView.setVisibility(View.VISIBLE);
        }
        else {
            overlayView.setVisibility(View.GONE);
        }

        if (obj.isHidden) {
            parentView.setAlpha(0.40f);
        }
        else {
            parentView.setAlpha(1.0f);
        }
    }
}
