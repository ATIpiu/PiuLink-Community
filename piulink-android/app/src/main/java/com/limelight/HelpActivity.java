package com.limelight;

import android.app.Activity;
import android.graphics.Bitmap;
import android.os.Build;
import android.os.Bundle;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.window.OnBackInvokedCallback;
import android.window.OnBackInvokedDispatcher;

import android.view.View;
import android.widget.FrameLayout;
import android.widget.ProgressBar;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;

public class HelpActivity extends Activity {

    private ProgressBar progress;
    private WebView webView;

    private boolean backCallbackRegistered;
    private OnBackInvokedCallback onBackInvokedCallback;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            onBackInvokedCallback = new OnBackInvokedCallback() {
                @Override
                public void onBackInvoked() {
                    // We should always be able to go back because we unregister our callback
                    // when we can't go back. Nonetheless, we will still check anyway.
                    if (webView.canGoBack()) {
                        webView.goBack();
                    }
                }
            };
        }

        webView = new WebView(this);
        FrameLayout page = new FrameLayout(this);
        page.addView(webView);
        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setIndeterminate(true);
        page.addView(progress, new FrameLayout.LayoutParams(-1,
                Math.round(3 * getResources().getDisplayMetrics().density)));
        setContentView(page);

        // These allow the user to zoom the page
        webView.getSettings().setBuiltInZoomControls(true);
        webView.getSettings().setDisplayZoomControls(false);

        // This sets the view to display the whole page by default
        webView.getSettings().setUseWideViewPort(true);
        webView.getSettings().setLoadWithOverviewMode(true);

        // This allows the links to places on the same page to work
        webView.getSettings().setJavaScriptEnabled(true);

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public void onPageStarted(WebView view, String url, Bitmap favicon) {
                progress.setVisibility(View.VISIBLE);
                refreshBackDispatchState();
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                if (url.equals(view.getUrl())) progress.setVisibility(View.GONE);
                refreshBackDispatchState();
            }

            @Override public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame()) progress.setVisibility(View.GONE);
            }
        });

        webView.loadUrl(getIntent().getData().toString());
    }

    private void refreshBackDispatchState() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (webView.canGoBack() && !backCallbackRegistered) {
                getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                        OnBackInvokedDispatcher.PRIORITY_DEFAULT, onBackInvokedCallback);
                backCallbackRegistered = true;
            }
            else if (!webView.canGoBack() && backCallbackRegistered) {
                getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(onBackInvokedCallback);
                backCallbackRegistered = false;
            }
        }
    }

    @Override
    protected void onDestroy() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (backCallbackRegistered) {
                getOnBackInvokedDispatcher().unregisterOnBackInvokedCallback(onBackInvokedCallback);
            }
        }

        super.onDestroy();
    }

    @Override
    // NOTE: This will NOT be called on Android 13+ with android:enableOnBackInvokedCallback="true"
    public void onBackPressed() {
        // Back goes back through the WebView history
        // until no more history remains
        if (webView.canGoBack()) {
            webView.goBack();
        }
        else {
            super.onBackPressed();
        }
    }
}
