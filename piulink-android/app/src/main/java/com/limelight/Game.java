package com.limelight;


import com.limelight.binding.PlatformBinding;
import com.limelight.binding.audio.AndroidAudioRenderer;
import com.limelight.binding.input.ControllerHandler;
import com.limelight.binding.input.KeyboardTranslator;
import com.limelight.binding.input.capture.InputCaptureManager;
import com.limelight.binding.input.capture.InputCaptureProvider;
import com.limelight.binding.input.touch.AbsoluteTouchContext;
import com.limelight.binding.input.touch.RelativeTouchContext;
import com.limelight.binding.input.driver.UsbDriverService;
import com.limelight.binding.input.evdev.EvdevListener;
import com.limelight.binding.input.touch.TouchContext;
import com.limelight.binding.input.virtual_controller.VirtualController;
import com.limelight.binding.video.CrashListener;
import com.limelight.binding.video.MediaCodecDecoderRenderer;
import com.limelight.binding.video.MediaCodecHelper;
import com.limelight.binding.video.PerfOverlayListener;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.NvConnectionListener;
import com.limelight.nvstream.StreamConfiguration;
import com.limelight.nvstream.http.ComputerDetails;
import com.limelight.nvstream.http.NvApp;
import com.limelight.nvstream.http.NvHTTP;
import com.limelight.nvstream.input.ControllerPacket;
import com.limelight.nvstream.input.KeyboardPacket;
import com.limelight.nvstream.input.MouseButtonPacket;
import com.limelight.nvstream.jni.MoonBridge;
import com.limelight.preferences.GlPreferences;
import com.limelight.preferences.PreferenceConfiguration;
import com.limelight.piulink.AutoBitrateManager;
import com.limelight.piulink.AutoCodecPolicy;
import com.limelight.piulink.StreamViewport;
import com.limelight.piulink.NetworkPathClassifier;
import com.limelight.piulink.P2pReconnectPolicy;
import com.limelight.piulink.ShortcutRailConfiguration;
import com.limelight.piulink.ShortcutChord;
import com.limelight.ui.GameGestures;
import com.limelight.ui.StreamView;
import com.limelight.utils.Dialog;
import com.limelight.utils.NetHelper;
import com.limelight.utils.ServerHelper;
import com.limelight.utils.ShortcutHelper;
import com.limelight.utils.SpinnerDialog;
import com.limelight.utils.UiHelper;

import android.annotation.SuppressLint;
import android.annotation.TargetApi;
import android.app.Activity;
import android.app.AlertDialog;
import android.app.PictureInPictureParams;
import android.app.Service;
import android.content.ComponentName;
import android.content.Context;
import com.limelight.piulink.RemoteMicrophone;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.Point;
import android.graphics.Rect;
import android.hardware.input.InputManager;
import android.media.AudioManager;
import android.net.ConnectivityManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.SystemClock;
import android.speech.RecognizerIntent;
import android.util.Rational;
import android.view.Display;
import android.view.InputDevice;
import android.view.HapticFeedbackConstants;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.Surface;
import android.view.SurfaceHolder;
import android.view.View;
import android.view.ViewGroup;
import android.view.View.OnGenericMotionListener;
import android.view.View.OnSystemUiVisibilityChangeListener;
import android.view.View.OnTouchListener;
import android.view.ViewTreeObserver;
import android.view.Window;
import android.view.WindowManager;
import android.view.animation.DecelerateInterpolator;
import android.widget.FrameLayout;
import android.view.inputmethod.InputMethodManager;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import java.io.ByteArrayInputStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.InetAddress;
import java.security.cert.CertificateException;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;


public class Game extends Activity implements SurfaceHolder.Callback,
        OnGenericMotionListener, OnTouchListener, NvConnectionListener, EvdevListener,
        OnSystemUiVisibilityChangeListener, GameGestures, StreamView.InputCallbacks,
        PerfOverlayListener, UsbDriverService.UsbDriverStateListener, View.OnKeyListener {
    private int lastButtonState = 0;

    // Only 2 touches are supported
    private final TouchContext[] touchContextMap = new TouchContext[2];

    private static final int REFERENCE_HORIZ_RES = 1280;
    private static final int REFERENCE_VERT_RES = 720;

    private static final int STYLUS_DOWN_DEAD_ZONE_DELAY = 100;
    private static final int STYLUS_DOWN_DEAD_ZONE_RADIUS = 20;

    private static final int STYLUS_UP_DEAD_ZONE_DELAY = 150;
    private static final int STYLUS_UP_DEAD_ZONE_RADIUS = 50;
    private static final int VOICE_INPUT_REQUEST = 0x5049;

    private ControllerHandler controllerHandler;
    private KeyboardTranslator keyboardTranslator;
    private VirtualController virtualController;

    private PreferenceConfiguration prefConfig;
    private SharedPreferences tombstonePrefs;

    private NvConnection conn;
    private SpinnerDialog spinner;
    private boolean displayedFailureDialog = false;
    private boolean connecting = false;
    private boolean connected = false;
    private long connectionStartedAt;
    private long stageStartedAt;
    private boolean autoEnterPip = false;
    private boolean streamPageStopped;
    private boolean backgroundTransitionRequested;
    private String backgroundSession;
    private boolean activityResumed;
    private final com.limelight.binding.input.MouseInputStats mouseInputStats =
            new com.limelight.binding.input.MouseInputStats();
    private final com.limelight.binding.input.MouseInputStats touchInputStats =
            new com.limelight.binding.input.MouseInputStats();
    private boolean surfaceCreated = false;
    private boolean attemptedConnection = false;
    private int suppressPipRefCount = 0;
    private String pcName;
    private String appName;
    private NvApp app;
    private float desiredRefreshRate;

    private InputCaptureProvider inputCaptureProvider;
    private final com.limelight.binding.input.RelativeMouseMotion relativeMouseMotion =
            new com.limelight.binding.input.RelativeMouseMotion();
    private int modifierFlags = 0;
    private boolean grabbedInput = true;
    private boolean cursorVisible = false;
    private final Handler cursorHandler = new Handler(Looper.getMainLooper());
    private int cursorUpdateGeneration;
    private boolean waitingForAllModifiersUp = false;
    private int specialKeyCode = KeyEvent.KEYCODE_UNKNOWN;
    private StreamView streamView;
    private long lastAbsTouchUpTime = 0;
    private long lastAbsTouchDownTime = 0;
    private float lastAbsTouchUpX, lastAbsTouchUpY;
    private float lastAbsTouchDownX, lastAbsTouchDownY;
    private final StreamViewport viewport = new StreamViewport();
    private long lastLocalViewportMotion;
    private float captureFractionX = 1f, captureFractionY = 1f;
    private long panFrameTime;
    private float panResponseMs;
    private final Runnable panFrame = new Runnable() {
        @Override public void run() {
            if (panFrameTime == 0 || streamView == null) return;
            long now = System.nanoTime();
            float elapsedMs = (now - panFrameTime) / 1_000_000f;
            float x = StreamViewport.approach(streamView.getTranslationX(), viewport.x, elapsedMs, panResponseMs);
            float y = StreamViewport.approach(streamView.getTranslationY(), viewport.y, elapsedMs, panResponseMs);
            streamView.setTranslationX(x);
            streamView.setTranslationY(y);
            if (x == viewport.x && y == viewport.y) panFrameTime = 0;
            else {
                panFrameTime = now;
                streamView.postOnAnimation(this);
            }
        }
    };
    private boolean pinching, pendingPinch;
    private float pinchSpan, pinchMidX, pinchMidY;
    private boolean softwareKeyboardVisible;
    private com.limelight.piulink.SessionInputHints inputHints;
    private boolean hostUsingMicrophone, demandMuted, demandCapture;
    private long mousePressedAt;
    private float mousePressedX, mousePressedY;
    private boolean voiceInputCapturePaused;
    private boolean voiceInputInFlight;
    private String pendingDictation;
    private static final int REQUEST_REMOTE_MIC = 48013;
    private X509Certificate serverCert;
    private RemoteMicrophone remoteMic;
    private TextView micButton, micBadge, micDetail;
    private String micDevice, micMessage;
    private boolean micPermissionPending, micPermissionGranted;
    private View keyboardLayoutRoot;
    private ViewTreeObserver.OnGlobalLayoutListener keyboardLayoutListener;

    private boolean isHidingOverlays;
    private TextView notificationOverlayView;
    private int requestedNotificationOverlayVisibility = View.GONE;
    private TextView performanceOverlayView;
    private ScrollView shortcutRail;
    private GridLayout shortcutRailItems;
    private View shortcutRailHandle;
    private View shortcutRailTrigger;
    private View streamMenuHandle;
    private View streamOverlayScrim;
    private LinearLayout streamMenuPanel;
    private LinearLayout streamMenuItems;
    private TextView localCursorMenuButton;
    private TextView performanceMenuButton;
    private TextView pipMenuButton;
    private TextView backgroundDurationMenuButton;
    private boolean streamMenuOpen;
    private boolean consumeOverlayGesture;
    private boolean shortcutRailOpen;
    private long suppressOverlayFallbackUntilMs;
    private boolean television;
    private final TvStreamKeys tvKeys = new TvStreamKeys();
    private AlertDialog tvReconnectDialog;
    private int overlayHatDirection;
    private int overlayHatYDirection;
    private String activeShortcutRailAction;
    private int activeShortcutRailKeyCode = KeyEvent.KEYCODE_UNKNOWN;
    private byte activeShortcutRailModifiers;

    private MediaCodecDecoderRenderer decoderRenderer;
    private boolean reportedCrash;

    private final Handler autoBitrateHandler = new Handler(Looper.getMainLooper());
    private final Handler reconnectHandler = new Handler(Looper.getMainLooper());
    private static final String EXTRA_P2P_RECONNECT_ATTEMPT = "PiuLinkP2pReconnectAttempt";
    private static final long P2P_RECONNECT_DELAY_MS = 1200;
    private static final long P2P_RECONNECT_RESET_MS = 30_000;
    private static final long CONTENT_ADAPTIVE_RETRY_MS = 750;
    private static final int CONTENT_ADAPTIVE_MAX_ATTEMPTS = 8;
    private AutoBitrateManager.Session autoBitrateSession;
    private long streamConnectedAtMs;
    private boolean videoStallReported;
    private int contentAdaptiveAttempts;
    private boolean contentAdaptiveControlTimedOut;
    private final Runnable contentAdaptiveControlRetry = new Runnable() {
        @Override
        public void run() {
            NvConnection connection = conn;
            if (!connected || connection == null || !connection.supportsPiuLinkControl()) {
                return;
            }

            int state = connection.getContentAdaptiveState();
            if (state >= 0) {
                contentAdaptiveControlTimedOut = false;
                LimeLog.info("PiuLink content-adaptive host encoding confirmed: " + state);
                return;
            }

            if (contentAdaptiveAttempts >= CONTENT_ADAPTIVE_MAX_ATTEMPTS) {
                contentAdaptiveControlTimedOut = true;
                LimeLog.warning("PiuLink content-adaptive host encoding confirmation timed out");
                return;
            }

            contentAdaptiveAttempts++;
            if (connection.setContentAdaptiveEncoding(prefConfig.contentAdaptiveEncoding)) {
                LimeLog.info("PiuLink content-adaptive host encoding requested, attempt " +
                        contentAdaptiveAttempts + ", enabled=" +
                        prefConfig.contentAdaptiveEncoding);
            }
            else {
                LimeLog.warning("PiuLink content-adaptive request could not be sent, attempt " +
                        contentAdaptiveAttempts);
            }
            autoBitrateHandler.postDelayed(this, CONTENT_ADAPTIVE_RETRY_MS);
        }
    };
    private final Runnable autoBitrateStableSample = new Runnable() {
        @Override
        public void run() {
            if (connected && autoBitrateSession != null) {
                long nowMs = android.os.SystemClock.elapsedRealtime();
                long decodeAgeMs = decoderRenderer.getLastDecodeUnitAgeMs();
                long renderAgeMs = decoderRenderer.getLastRenderedFrameAgeMs();
                long connectedForMs = nowMs - streamConnectedAtMs;
                boolean receiveStalled = decodeAgeMs >= 5_000 ||
                        (decodeAgeMs < 0 && connectedForMs >= 5_000);
                boolean renderStalled = renderAgeMs >= 5_000 ||
                        (renderAgeMs < 0 && connectedForMs >= 5_000);
                if ((receiveStalled || renderStalled) && !videoStallReported) {
                    videoStallReported = true;
                    LimeLog.errEvent("stream.android.video_stalled", "stream",
                            "video stopped after connection", null, null,
                            LimeLog.fields("receive_stalled", receiveStalled,
                                    "render_stalled", renderStalled,
                                    "decode_unit_age_ms", decodeAgeMs,
                                    "render_age_ms", renderAgeMs,
                                    "connected_for_ms", connectedForMs));
                } else if (!receiveStalled && !renderStalled && videoStallReported) {
                    videoStallReported = false;
                    LimeLog.infoEvent("stream.android.video_resumed", "stream",
                            "video resumed after stall", null, null);
                }
                autoBitrateHandler.postDelayed(this, 5_000);
            }
        }
    };

    private WifiManager.WifiLock highPerfWifiLock;
    private WifiManager.WifiLock lowLatencyWifiLock;

    private boolean connectedToUsbDriverService = false;
    private ServiceConnection usbDriverServiceConnection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName componentName, IBinder iBinder) {
            UsbDriverService.UsbDriverBinder binder = (UsbDriverService.UsbDriverBinder) iBinder;
            binder.setListener(controllerHandler);
            binder.setStateListener(Game.this);
            binder.start();
            connectedToUsbDriverService = true;
        }

        @Override
        public void onServiceDisconnected(ComponentName componentName) {
            connectedToUsbDriverService = false;
        }
    };

    public static final String EXTRA_HOST = "Host";
    public static final String EXTRA_PORT = "Port";
    public static final String EXTRA_HTTPS_PORT = "HttpsPort";
    public static final String EXTRA_APP_NAME = "AppName";
    public static final String EXTRA_APP_ID = "AppId";
    public static final String EXTRA_UNIQUEID = "UniqueId";
    public static final String EXTRA_PC_UUID = "UUID";
    public static final String EXTRA_PC_NAME = "PcName";
    public static final String EXTRA_APP_HDR = "HDR";
    public static final String EXTRA_SERVER_CERT = "ServerCert";
    public static final String EXTRA_STREAM_WIDTH = "PiuLinkStreamWidth";
    public static final String EXTRA_STREAM_HEIGHT = "PiuLinkStreamHeight";
    public static final String EXTRA_CONFIRMED_SWITCH_FROM = "PiuLinkConfirmedSwitchFrom";
    public static final String EXTRA_DISPLAY_PREPARATION = "PiuLinkDisplayPreparation";
    private Intent replacementStreamIntent;
    public static final String EXTRA_STREAM_FPS = "PiuLinkStreamFps";

    public static volatile Game activeInstance = null;

    /** Reopen this device's existing stream window from its app library or shortcut. */
    public static boolean resumeActiveStream(Activity parent, String computerId, int appId) {
        Game game = activeInstance;
        if (game == null || !game.connected || game.leavingStream || game.isFinishing() || game.isDestroyed()) {
            return false;
        }
        Intent current = game.getIntent();
        if (computerId == null || !computerId.equals(current.getStringExtra(EXTRA_PC_UUID))
                || appId != current.getIntExtra(EXTRA_APP_ID, StreamConfiguration.INVALID_APP_ID)) {
            return false;
        }
        parent.startActivity(new Intent(current).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP));
        return true;
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        television = UiHelper.isTelevision(this);

        UiHelper.setLocale(this);

        // We don't want a title bar
        requestWindowFeature(Window.FEATURE_NO_TITLE);

        // Full-screen
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);

        // If we're going to use immersive mode, we want to have
        // the entire screen
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
                        View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
                        View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);

        getWindow().addFlags(WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN);

        // Listen for UI visibility events
        getWindow().getDecorView().setOnSystemUiVisibilityChangeListener(this);

        // Change volume button behavior
        setVolumeControlStream(AudioManager.STREAM_MUSIC);

        // Inflate the content
        setContentView(R.layout.activity_game);

        // Start the spinner
        spinner = SpinnerDialog.displayDialog(this, getResources().getString(R.string.conn_establishing_title),
                getResources().getString(R.string.conn_establishing_msg), true);

        // Read the stream preferences
        prefConfig = PreferenceConfiguration.readPreferences(this);
        int streamWidth = getIntent().getIntExtra(EXTRA_STREAM_WIDTH, 0);
        int streamHeight = getIntent().getIntExtra(EXTRA_STREAM_HEIGHT, 0);
        if (streamWidth > 0 && streamHeight > 0) {
            prefConfig.smartDisplay = false;
            prefConfig.width = streamWidth;
            prefConfig.height = streamHeight;
            LimeLog.info("Using selected stream resolution " +
                    streamWidth + "x" + streamHeight);
        }
        int streamFps = getIntent().getIntExtra(EXTRA_STREAM_FPS, 0);
        if (streamFps >= 30 && streamFps <= 360) {
            prefConfig.fps = streamFps;
            LimeLog.info("Using selected stream frame rate " + streamFps + " FPS");
        }
        tombstonePrefs = Game.this.getSharedPreferences("DecoderTombstone", 0);
        // Enter landscape unless we're on a square screen
        setPreferredOrientationForCurrentDisplay();

        if (prefConfig.stretchVideo || shouldIgnoreInsetsForResolution(prefConfig.width, prefConfig.height)) {
            // Allow the activity to layout under notches if the fill-screen option
            // was turned on by the user or it's a full-screen native resolution
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                getWindow().getAttributes().layoutInDisplayCutoutMode =
                        WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS;
            }
            else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                getWindow().getAttributes().layoutInDisplayCutoutMode =
                        WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES;
            }
        }

        // Listen for non-touch events on the game surface
        streamView = findViewById(R.id.surfaceView);
        streamView.setOnGenericMotionListener(this);
        streamView.setOnKeyListener(this);
        streamView.setInputCallbacks(this);
        // StreamView exposes an InputConnection for forwarding text to the host. Android TV
        // may interpret that as an invitation to open the IME as soon as the stream gains
        // focus, so keep implicit soft-input display disabled until the user asks for it.
        streamView.setTextInputEnabled(false);

        setupShortcutRail();
        setupStreamMenu();
        getWindow().getDecorView().post(this::applyResponsiveStreamOverlayLayout);
        streamOverlayScrim = findViewById(R.id.streamOverlayScrim);
        streamOverlayScrim.setOnClickListener(view -> {
            releaseActiveShortcutRailChord();
            if (shortcutRailOpen) setShortcutRailOpen(false, true);
            if (streamMenuOpen) setStreamMenuOpen(false, true);
            streamView.requestFocus();
        });
        setupSoftwareKeyboardTracking();

        // Listen for touch events on the background touch view to enable trackpad mode
        // to work on areas outside of the StreamView itself. We use a separate View
        // for this rather than just handling it at the Activity level, because that
        // allows proper touch splitting, which the OSC relies upon.
        View backgroundTouchView = findViewById(R.id.backgroundTouchView);
        backgroundTouchView.setOnTouchListener(this);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            // Request unbuffered input event dispatching for all input classes we handle here.
            // Without this, input events are buffered to be delivered in lock-step with VBlank,
            // artificially increasing input latency while streaming.
            streamView.requestUnbufferedDispatch(
                    InputDevice.SOURCE_CLASS_BUTTON | // Keyboards
                    InputDevice.SOURCE_CLASS_JOYSTICK | // Gamepads
                    InputDevice.SOURCE_CLASS_POINTER | // Touchscreens and mice (w/o pointer capture)
                    InputDevice.SOURCE_CLASS_POSITION | // Touchpads
                    InputDevice.SOURCE_CLASS_TRACKBALL // Mice (pointer capture)
            );
            backgroundTouchView.requestUnbufferedDispatch(
                    InputDevice.SOURCE_CLASS_BUTTON | // Keyboards
                    InputDevice.SOURCE_CLASS_JOYSTICK | // Gamepads
                    InputDevice.SOURCE_CLASS_POINTER | // Touchscreens and mice (w/o pointer capture)
                    InputDevice.SOURCE_CLASS_POSITION | // Touchpads
                    InputDevice.SOURCE_CLASS_TRACKBALL // Mice (pointer capture)
            );
        }

        notificationOverlayView = findViewById(R.id.notificationOverlay);

        performanceOverlayView = findViewById(R.id.performanceOverlay);

        inputCaptureProvider = InputCaptureManager.getInputCaptureProvider(this, this);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            streamView.setOnCapturedPointerListener(new View.OnCapturedPointerListener() {
                @Override
                public boolean onCapturedPointer(View view, MotionEvent motionEvent) {
                    return handleMotionEvent(view, motionEvent);
                }
            });
        }

        // Warn the user if they're on a metered connection
        ConnectivityManager connMgr = (ConnectivityManager) getSystemService(Context.CONNECTIVITY_SERVICE);
        if (connMgr.isActiveNetworkMetered()) {
            displayTransientMessage(getResources().getString(R.string.conn_metered));
        }

        // Make sure Wi-Fi is fully powered up
        WifiManager wifiMgr = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        try {
            highPerfWifiLock = wifiMgr.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "Moonlight High Perf Lock");
            highPerfWifiLock.setReferenceCounted(false);
            highPerfWifiLock.acquire();

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                lowLatencyWifiLock = wifiMgr.createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "Moonlight Low Latency Lock");
                lowLatencyWifiLock.setReferenceCounted(false);
                lowLatencyWifiLock.acquire();
            }
        } catch (SecurityException e) {
            // Some Samsung Galaxy S10+/S10e devices throw a SecurityException from
            // WifiLock.acquire() even though we have android.permission.WAKE_LOCK in our manifest.
            e.printStackTrace();
        }

        appName = Game.this.getIntent().getStringExtra(EXTRA_APP_NAME);
        pcName = Game.this.getIntent().getStringExtra(EXTRA_PC_NAME);

        String host = Game.this.getIntent().getStringExtra(EXTRA_HOST);
        prefConfig.preferAv1ForAuto = AutoCodecPolicy.preferAv1(host, prefConfig.width, prefConfig.height,
                connMgr.isActiveNetworkMetered());
        LimeLog.info("PiuLink automatic codec mode: " +
                (prefConfig.preferAv1ForAuto ? "efficiency (AV1 when hardware-capable)" : "balanced (HEVC/H.264)"));
        int port = Game.this.getIntent().getIntExtra(EXTRA_PORT, NvHTTP.DEFAULT_HTTP_PORT);
        int httpsPort = Game.this.getIntent().getIntExtra(EXTRA_HTTPS_PORT, 0); // 0 is treated as unknown
        int appId = Game.this.getIntent().getIntExtra(EXTRA_APP_ID, StreamConfiguration.INVALID_APP_ID);
        String uniqueId = Game.this.getIntent().getStringExtra(EXTRA_UNIQUEID);
        String pcUuid = Game.this.getIntent().getStringExtra(EXTRA_PC_UUID);
        boolean appSupportsHdr = Game.this.getIntent().getBooleanExtra(EXTRA_APP_HDR, false);
        byte[] derCertData = Game.this.getIntent().getByteArrayExtra(EXTRA_SERVER_CERT);

        app = new NvApp(appName != null ? appName : "app", appId, appSupportsHdr);

        try {
            if (derCertData != null) {
                serverCert = (X509Certificate) CertificateFactory.getInstance("X.509")
                        .generateCertificate(new ByteArrayInputStream(derCertData));
            }
        } catch (CertificateException e) {
            e.printStackTrace();
        }

        if (appId == StreamConfiguration.INVALID_APP_ID) {
            finish();
            return;
        }

        if (prefConfig.autoBitrate) {
            autoBitrateSession = new AutoBitrateManager(this).beginSession(
                    host, prefConfig.saveDataOnMeteredNetwork,
                    prefConfig.width, prefConfig.height, prefConfig.fps, prefConfig.autoBitrateMode);
            LimeLog.info("Auto bitrate initial target " + autoBitrateSession.getSelectedKbps() +
                    " Kbps, negotiated ceiling=" + autoBitrateSession.getCeilingKbps() + " Kbps" +
                    " route=" + autoBitrateSession.getRoute() +
                    " downstreamEstimate=" + autoBitrateSession.getDownstreamEstimateKbps() + " Kbps");
            // Select once before the handshake. Streaming telemetry is diagnostic only.
            prefConfig.bitrate = autoBitrateSession.getSelectedKbps();
        }

        // Initialize the MediaCodec helper before creating the decoder
        GlPreferences glPrefs = GlPreferences.readPreferences(this);
        MediaCodecHelper.initialize(this, glPrefs.glRenderer);

        // Check if the user has enabled HDR
        boolean willStreamHdr = false;
        if (prefConfig.enableHdr) {
            // Start our HDR checklist
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                Display display = getWindowManager().getDefaultDisplay();
                Display.HdrCapabilities hdrCaps = display.getHdrCapabilities();

                // We must now ensure our display is compatible with HDR10
                if (hdrCaps != null) {
                    // getHdrCapabilities() returns null on Lenovo Lenovo Mirage Solo (vega), Android 8.0
                    for (int hdrType : hdrCaps.getSupportedHdrTypes()) {
                        if (hdrType == Display.HdrCapabilities.HDR_TYPE_HDR10) {
                            willStreamHdr = true;
                            break;
                        }
                    }
                }

                if (!willStreamHdr) {
                    // Nope, no HDR for us :(
                    Toast.makeText(this, "Display does not support HDR10", Toast.LENGTH_LONG).show();
                }
            }
            else {
                Toast.makeText(this, "HDR requires Android 7.0 or later", Toast.LENGTH_LONG).show();
            }
        }

        // Check if the user has enabled performance stats overlay
        if (prefConfig.enablePerfOverlay) {
            performanceOverlayView.setVisibility(View.VISIBLE);
        }

        decoderRenderer = new MediaCodecDecoderRenderer(
                this,
                prefConfig,
                new CrashListener() {
                    @Override
                    public void notifyCrash(Exception e) {
                        // The MediaCodec instance is going down due to a crash
                        // let's tell the user something when they open the app again

                        // We must use commit because the app will crash when we return from this function
                        tombstonePrefs.edit().putInt("CrashCount", tombstonePrefs.getInt("CrashCount", 0) + 1).commit();
                        reportedCrash = true;
                    }
                },
                tombstonePrefs.getInt("CrashCount", 0),
                connMgr.isActiveNetworkMetered(),
                willStreamHdr,
                glPrefs.glRenderer,
                this);

        // Don't stream HDR if the decoder can't support it
        if (willStreamHdr && !decoderRenderer.isHevcMain10Hdr10Supported() && !decoderRenderer.isAv1Main10Supported()) {
            willStreamHdr = false;
            Toast.makeText(this, "Decoder does not support HDR10 profile", Toast.LENGTH_LONG).show();
        }

        // Display a message to the user if HEVC was forced on but we still didn't find a decoder
        if (prefConfig.videoFormat == PreferenceConfiguration.FormatOption.FORCE_HEVC && !decoderRenderer.isHevcSupported()) {
            Toast.makeText(this, "No HEVC decoder found", Toast.LENGTH_LONG).show();
        }

        // Display a message to the user if AV1 was forced on but we still didn't find a decoder
        if (prefConfig.videoFormat == PreferenceConfiguration.FormatOption.FORCE_AV1 && !decoderRenderer.isAv1Supported()) {
            Toast.makeText(this, "No AV1 decoder found", Toast.LENGTH_LONG).show();
        }

        // H.264 is always supported
        int supportedVideoFormats = MoonBridge.VIDEO_FORMAT_H264;
        if (decoderRenderer.isHevcSupported()) {
            supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_H265;
            if (willStreamHdr && decoderRenderer.isHevcMain10Hdr10Supported()) {
                supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_H265_MAIN10;
            }
        }
        if (decoderRenderer.isAv1Supported()) {
            supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_AV1_MAIN8;
            if (willStreamHdr && decoderRenderer.isAv1Main10Supported()) {
                supportedVideoFormats |= MoonBridge.VIDEO_FORMAT_AV1_MAIN10;
            }
        }

        int gamepadMask = ControllerHandler.getAttachedControllerMask(this);
        if (!prefConfig.multiController) {
            // Always set gamepad 1 present for when multi-controller is
            // disabled for games that don't properly support detection
            // of gamepads removed and replugged at runtime.
            gamepadMask = 1;
        }
        if (prefConfig.onscreenController) {
            // If we're using OSC, always set at least gamepad 1.
            gamepadMask |= 1;
        }

        // Set to the optimal mode for streaming
        float displayRefreshRate = prepareDisplayForRendering();
        LimeLog.info("Display refresh rate: "+displayRefreshRate);

        // If the user requested frame pacing using a capped FPS, we will need to change our
        // desired FPS setting here in accordance with the active display refresh rate.
        int roundedRefreshRate = Math.round(displayRefreshRate);
        int chosenFrameRate = prefConfig.fps;
        if (prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_CAP_FPS) {
            if (prefConfig.fps >= roundedRefreshRate) {
                if (prefConfig.fps > roundedRefreshRate + 3) {
                    // Use frame drops when rendering above the screen frame rate
                    prefConfig.framePacing = PreferenceConfiguration.FRAME_PACING_BALANCED;
                    LimeLog.info("Using drop mode for FPS > Hz");
                } else if (roundedRefreshRate <= 49) {
                    // Let's avoid clearly bogus refresh rates and fall back to legacy rendering
                    prefConfig.framePacing = PreferenceConfiguration.FRAME_PACING_BALANCED;
                    LimeLog.info("Bogus refresh rate: " + roundedRefreshRate);
                }
                else {
                    chosenFrameRate = roundedRefreshRate - 1;
                    LimeLog.info("Adjusting FPS target for screen to " + chosenFrameRate);
                }
            }
        }

        int confirmedSwitchFrom = savedInstanceState == null ? getIntent().getIntExtra(EXTRA_CONFIRMED_SWITCH_FROM, 0) : 0;
        getIntent().removeExtra(EXTRA_CONFIRMED_SWITCH_FROM);
        StreamConfiguration config = new StreamConfiguration.Builder()
                .setConfirmedSwitchFrom(confirmedSwitchFrom)
                .setResolution(prefConfig.width, prefConfig.height)
                .setDisplayPreparationId(getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION))
                .setLaunchRefreshRate(prefConfig.fps)
                .setRefreshRate(chosenFrameRate)
                .setApp(app)
                .setBitrate(prefConfig.bitrate)
                .setEnableSops(prefConfig.smartDisplay || prefConfig.enableSops)
                .enableAdaptiveResolution(prefConfig.smartDisplay)
                .enableLocalAudioPlayback(prefConfig.playHostAudio)
                .setMaxPacketSize(com.limelight.computers.DirectInviteTunnel.isEndpoint(host) ? 1024 : 1392)
                .setRemoteConfiguration(com.limelight.computers.DirectInviteTunnel.isEndpoint(host) ?
                        StreamConfiguration.STREAM_CFG_REMOTE : StreamConfiguration.STREAM_CFG_AUTO)
                .setSupportedVideoFormats(supportedVideoFormats)
                .setAttachedGamepadMask(gamepadMask)
                .setClientRefreshRateX100((int)(displayRefreshRate * 100))
                .setAudioConfiguration(prefConfig.audioConfiguration)
                .setColorSpace(decoderRenderer.getPreferredColorSpace())
                .setColorRange(decoderRenderer.getPreferredColorRange())
                .setPersistGamepadsAfterDisconnect(!prefConfig.multiController)
                .build();

        // Initialize the connection
        conn = new NvConnection(getApplicationContext(),
                new ComputerDetails.AddressTuple(host, port),
                httpsPort, uniqueId, config,
                PlatformBinding.getCryptoProvider(this), serverCert);
        controllerHandler = new ControllerHandler(this, conn, this, prefConfig);
        keyboardTranslator = new KeyboardTranslator();

        InputManager inputManager = (InputManager) getSystemService(Context.INPUT_SERVICE);
        inputManager.registerInputDeviceListener(keyboardTranslator, null);

        String sessionId = getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION);
        if (sessionId != null && serverCert != null && UiHelper.isTouchscreenClient(this)) {
            inputHints = new com.limelight.piulink.SessionInputHints(this, host, sessionId, serverCert,
                    new com.limelight.piulink.SessionInputHints.Listener() {
                        public boolean canShowKeyboard() { return canAutoShowKeyboard(); }
                        public void showKeyboard() { openSoftwareKeyboard(true); }
                        public void viewportPosition(float x, float y, boolean caret, long requestedAt) {
                            if (pinching) return;
                            if (!caret && requestedAt <= lastLocalViewportMotion) return;
                            viewport.position(0.5f + (x - 0.5f) * captureFractionX,
                                    0.5f + (y - 0.5f) * captureFractionY, caret);
                            if (caret) applyViewport();
                            else animatePointerPan(false);
                        }
                        public void microphoneUse(boolean active) {
                            boolean changed = hostUsingMicrophone != active;
                            hostUsingMicrophone = active;
                            if (changed && active && canSendRemoteText()) {
                                Toast.makeText(Game.this, R.string.host_mic_in_use, Toast.LENGTH_LONG).show();
                            }
                            if (!active) {
                                demandMuted = false;
                                if (demandCapture) stopRemoteMicrophone();
                            } else if (autoMicEnabled(Game.this, getIntent().getStringExtra(EXTRA_PC_UUID)) && !demandMuted && remoteMic == null && !micPermissionPending &&
                                    canSendRemoteText() && checkSelfPermission(android.Manifest.permission.RECORD_AUDIO)
                                    == PackageManager.PERMISSION_GRANTED) {
                                demandCapture = true;
                                toggleRemoteMicrophone();
                            }
                            if (changed) updateMicControls();
                        }
                    });
        }
        // Initialize touch contexts
        for (int i = 0; i < touchContextMap.length; i++) {
            if (!prefConfig.touchscreenTrackpad) {
                touchContextMap[i] = new AbsoluteTouchContext(conn, i, streamView, this::onRemoteTap, this::viewportAbsolute);
            }
            else {
                touchContextMap[i] = new RelativeTouchContext(conn, i,
                        REFERENCE_HORIZ_RES, REFERENCE_VERT_RES,
                        streamView, prefConfig, this::onRemoteTap, (dx, dy) -> viewportRelative(dx, dy, prefConfig.absoluteMouseMode));
            }
        }

        if (prefConfig.onscreenController) {
            // create virtual onscreen controller
            virtualController = new VirtualController(controllerHandler,
                    (FrameLayout)streamView.getParent(),
                    this);
            virtualController.refreshLayout();
            virtualController.show();
        }

        if (prefConfig.usbDriver) {
            // Start the USB driver
            bindService(new Intent(this, UsbDriverService.class),
                    usbDriverServiceConnection, Service.BIND_AUTO_CREATE);
        }

        if (!decoderRenderer.isAvcSupported()) {
            if (spinner != null) {
                spinner.dismiss();
                spinner = null;
            }

            // If we can't find an AVC decoder, we can't proceed
            Dialog.displayDialog(this, getResources().getString(R.string.conn_error_title),
                    "This device or ROM doesn't support hardware accelerated H.264 playback.", true);
            return;
        }

        // The connection will be started when the surface gets created
        streamView.getHolder().addCallback(this);
    }

    private void setPreferredOrientationForCurrentDisplay() {
        Display display = getWindowManager().getDefaultDisplay();

        // For semi-square displays, we use more complex logic to determine which orientation to use (if any)
        if (PreferenceConfiguration.isSquarishScreen(display)) {
            int desiredOrientation = Configuration.ORIENTATION_UNDEFINED;

            // OSC doesn't properly support portrait displays, so don't use it in portrait mode by default
            if (prefConfig.onscreenController) {
                desiredOrientation = Configuration.ORIENTATION_LANDSCAPE;
            }

            // For native resolution, we will lock the orientation to the one that matches the specified resolution
            if (PreferenceConfiguration.isNativeResolution(prefConfig.width, prefConfig.height)) {
                if (prefConfig.width > prefConfig.height) {
                    desiredOrientation = Configuration.ORIENTATION_LANDSCAPE;
                }
                else {
                    desiredOrientation = Configuration.ORIENTATION_PORTRAIT;
                }
            }

            if (desiredOrientation == Configuration.ORIENTATION_LANDSCAPE) {
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE);
            }
            else if (desiredOrientation == Configuration.ORIENTATION_PORTRAIT) {
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_PORTRAIT);
            }
            else {
                // If we don't have a reason to lock to portrait or landscape, allow any orientation
                setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_FULL_USER);
            }
        }
        else {
            // For regular displays, we always request landscape
            setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_USER_LANDSCAPE);
        }
    }

    @Override
    public void onConfigurationChanged(Configuration newConfig) {
        super.onConfigurationChanged(newConfig);

        // Set requested orientation for possible new screen size
        setPreferredOrientationForCurrentDisplay();

        if (virtualController != null) {
            // Refresh layout of OSC for possible new screen size
            virtualController.refreshLayout();
        }

        // Hide on-screen overlays in PiP mode
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            if (isInPictureInPictureMode()) {
                isHidingOverlays = true;

                if (virtualController != null) {
                    virtualController.hide();
                }

                performanceOverlayView.setVisibility(View.GONE);
                notificationOverlayView.setVisibility(View.GONE);
                if (shortcutRail != null) {
                    shortcutRail.animate().cancel();
                    shortcutRail.setVisibility(View.GONE);
                }
                if (shortcutRailHandle != null) {
                    shortcutRailHandle.setVisibility(View.GONE);
                }
                if (streamMenuHandle != null) streamMenuHandle.setVisibility(View.GONE);
                if (streamMenuPanel != null) streamMenuPanel.setVisibility(View.GONE);

                // A display/orientation callback can arrive while onCreate() is still building
                // the streaming input stack. There are no sensors to suspend until that is done.
                if (controllerHandler != null) {
                    controllerHandler.disableSensors();
                }

                // Update GameManager state to indicate we're in PiP (still gaming, but interruptible)
                UiHelper.notifyStreamEnteringPiP(this);
            }
            else {
                isHidingOverlays = false;

                // Restore overlays to previous state when leaving PiP

                if (virtualController != null) {
                    virtualController.show();
                }

                if (prefConfig.enablePerfOverlay) {
                    performanceOverlayView.setVisibility(View.VISIBLE);
                }

                notificationOverlayView.setVisibility(requestedNotificationOverlayVisibility);
                restoreShortcutRailVisibility();
                if (streamMenuHandle != null) streamMenuHandle.setVisibility(View.VISIBLE);
                if (streamMenuPanel != null && streamMenuOpen) {
                    streamMenuPanel.setVisibility(View.VISIBLE);
                }

                // See the matching PiP-entry guard above: configuration changes are allowed
                // before ControllerHandler is initialized.
                if (controllerHandler != null) {
                    controllerHandler.enableSensors();
                }

                // Update GameManager state to indicate we're out of PiP (gaming, non-interruptible)
                UiHelper.notifyStreamExitingPiP(this);
            }
        }
    }

    @TargetApi(Build.VERSION_CODES.O)
    private PictureInPictureParams getPictureInPictureParams(boolean autoEnter) {
        PictureInPictureParams.Builder builder =
                new PictureInPictureParams.Builder()
                        .setAspectRatio(pictureInPictureAspectRatio())
                        .setSourceRectHint(new Rect(
                                streamView.getLeft(), streamView.getTop(),
                                streamView.getRight(), streamView.getBottom()));

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            builder.setAutoEnterEnabled(autoEnter);
            builder.setSeamlessResizeEnabled(true);
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (appName != null) {
                builder.setTitle(appName);
                if (pcName != null) {
                    builder.setSubtitle(pcName);
                }
            }
            else if (pcName != null) {
                builder.setTitle(pcName);
            }
        }

        return builder.build();
    }

    private Rational pictureInPictureAspectRatio() {
        int width = Math.max(1, prefConfig.width), height = Math.max(1, prefConfig.height);
        if ((long)width * 100 > (long)height * 239) return new Rational(239, 100);
        if ((long)height * 100 > (long)width * 239) return new Rational(100, 239);
        return new Rational(width, height);
    }

    private void updatePipAutoEnter() {
        if (!getPackageManager().hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)) {
            autoEnterPip = false;
            return;
        }

        // Keep the established transport alive while the user briefly switches apps. Explicit
        // Back navigation still ends the Sunshine session in onBackPressed().
        boolean autoEnter = prefConfig.enablePip && connected && !leavingStream && !micPermissionPending && suppressPipRefCount == 0;

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            setPictureInPictureParams(getPictureInPictureParams(autoEnter));
        }
        else {
            autoEnterPip = autoEnter;
        }
    }

    public void setMetaKeyCaptureState(boolean enabled) {
        // This uses custom APIs present on some Samsung devices to allow capture of
        // meta key events while streaming.
        try {
            Class<?> semWindowManager = Class.forName("com.samsung.android.view.SemWindowManager");
            Method getInstanceMethod = semWindowManager.getMethod("getInstance");
            Object manager = getInstanceMethod.invoke(null);

            if (manager != null) {
                Class<?>[] parameterTypes = new Class<?>[2];
                parameterTypes[0] = ComponentName.class;
                parameterTypes[1] = boolean.class;
                Method requestMetaKeyEventMethod = semWindowManager.getDeclaredMethod("requestMetaKeyEvent", parameterTypes);
                requestMetaKeyEventMethod.invoke(manager, this.getComponentName(), enabled);
            }
            else {
                LimeLog.warning("SemWindowManager.getInstance() returned null");
            }
        } catch (ClassNotFoundException e) {
            e.printStackTrace();
        } catch (NoSuchMethodException e) {
            e.printStackTrace();
        } catch (InvocationTargetException e) {
            e.printStackTrace();
        } catch (IllegalAccessException e) {
            e.printStackTrace();
        }
    }

    @Override
    public void onUserLeaveHint() {
        super.onUserLeaveHint();

        if (prefConfig.enablePip && connected && !leavingStream && !micPermissionPending && suppressPipRefCount == 0 &&
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            try {
                backgroundTransitionRequested = enterPictureInPictureMode(
                        getPictureInPictureParams(false));
                if (backgroundTransitionRequested) {
                    startBackgroundStreamService();
                    return;
                }
            }
            catch (IllegalStateException | IllegalArgumentException exception) {
                LimeLog.warning("Unable to enter background PiP: " + exception.getMessage());
            }
        }

        // PiP is only supported on Oreo and later, and we don't need to manually enter PiP on
        // Android S and later. On Android R, we will use onPictureInPictureRequested() instead.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            if (autoEnterPip) {
                try {
                    // This has thrown all sorts of weird exceptions on Samsung devices
                    // running Oreo. Just eat them and close gracefully on leave, rather
                    // than crashing.
                    enterPictureInPictureMode(getPictureInPictureParams(false));
                } catch (Exception e) {
                    e.printStackTrace();
                }
            }
        }
    }

    @Override
    @TargetApi(Build.VERSION_CODES.R)
    public boolean onPictureInPictureRequested() {
        // Enter PiP when requested unless we're on Android 12 which supports auto-enter.
        if (autoEnterPip && Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
            enterPictureInPictureMode(getPictureInPictureParams(false));
        }
        return true;
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) { startGrantedMicrophone(); deliverDictation(); }

        // We can't guarantee the state of modifiers keys which may have
        // lifted while focus was not on us. Clear the modifier state.
        if (!hasFocus) {
            if (remoteMic != null) {
                stopRemoteMicrophone();
            }
            releaseForwardedKeys();
            if (inputHints != null) inputHints.cancelTap();
        }
        syncKeyboardOwner();

        // With Android native pointer capture, capture is lost when focus is lost,
        // so it must be requested again when focus is regained.
        inputCaptureProvider.onWindowFocusChanged(hasFocus);
        if (!hasFocus) {
            relativeMouseMotion.reset();
        }
    }

    private boolean isRefreshRateEqualMatch(float refreshRate) {
        return refreshRate >= prefConfig.fps &&
                refreshRate <= prefConfig.fps + 3;
    }

    private boolean isRefreshRateGoodMatch(float refreshRate) {
        return refreshRate >= prefConfig.fps &&
                Math.round(refreshRate) % prefConfig.fps <= 3;
    }

    private boolean shouldIgnoreInsetsForResolution(int width, int height) {
        // Never ignore insets for non-native resolutions
        if (!PreferenceConfiguration.isNativeResolution(width, height)) {
            return false;
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Display display = getWindowManager().getDefaultDisplay();
            for (Display.Mode candidate : display.getSupportedModes()) {
                // Ignore insets if this is an exact match for the display resolution
                if ((width == candidate.getPhysicalWidth() && height == candidate.getPhysicalHeight()) ||
                        (height == candidate.getPhysicalWidth() && width == candidate.getPhysicalHeight())) {
                    return true;
                }
            }
        }

        return false;
    }

    private boolean mayReduceRefreshRate() {
        return prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_CAP_FPS ||
                prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_MAX_SMOOTHNESS ||
                (prefConfig.framePacing == PreferenceConfiguration.FRAME_PACING_BALANCED && prefConfig.reduceRefreshRate);
    }

    private float prepareDisplayForRendering() {
        Display display = getWindowManager().getDefaultDisplay();
        WindowManager.LayoutParams windowLayoutParams = getWindow().getAttributes();
        float displayRefreshRate;

        // On M, we can explicitly set the optimal display mode
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Display.Mode bestMode = display.getMode();
            boolean isNativeResolutionStream = PreferenceConfiguration.isNativeResolution(prefConfig.width, prefConfig.height);
            boolean refreshRateIsGood = isRefreshRateGoodMatch(bestMode.getRefreshRate());
            boolean refreshRateIsEqual = isRefreshRateEqualMatch(bestMode.getRefreshRate());

            LimeLog.info("Current display mode: "+bestMode.getPhysicalWidth()+"x"+
                    bestMode.getPhysicalHeight()+"x"+bestMode.getRefreshRate());

            for (Display.Mode candidate : display.getSupportedModes()) {
                boolean refreshRateReduced = candidate.getRefreshRate() < bestMode.getRefreshRate();
                boolean resolutionReduced = candidate.getPhysicalWidth() < bestMode.getPhysicalWidth() ||
                        candidate.getPhysicalHeight() < bestMode.getPhysicalHeight();
                boolean resolutionFitsStream = candidate.getPhysicalWidth() >= prefConfig.width &&
                        candidate.getPhysicalHeight() >= prefConfig.height;

                LimeLog.info("Examining display mode: "+candidate.getPhysicalWidth()+"x"+
                        candidate.getPhysicalHeight()+"x"+candidate.getRefreshRate());

                if (candidate.getPhysicalWidth() > 4096 && prefConfig.width <= 4096) {
                    // Avoid resolutions options above 4K to be safe
                    continue;
                }

                // On non-4K streams, we force the resolution to never change unless it's above
                // 60 FPS, which may require a resolution reduction due to HDMI bandwidth limitations,
                // or it's a native resolution stream.
                if (prefConfig.width < 3840 && prefConfig.fps <= 60 && !isNativeResolutionStream) {
                    if (display.getMode().getPhysicalWidth() != candidate.getPhysicalWidth() ||
                            display.getMode().getPhysicalHeight() != candidate.getPhysicalHeight()) {
                        continue;
                    }
                }

                // Make sure the resolution doesn't regress unless if it's over 60 FPS
                // where we may need to reduce resolution to achieve the desired refresh rate.
                if (resolutionReduced && !(prefConfig.fps > 60 && resolutionFitsStream)) {
                    continue;
                }

                if (mayReduceRefreshRate() && refreshRateIsEqual && !isRefreshRateEqualMatch(candidate.getRefreshRate())) {
                    // If we had an equal refresh rate and this one is not, skip it. In min latency
                    // mode, we want to always prefer the highest frame rate even though it may cause
                    // microstuttering.
                    continue;
                }
                else if (refreshRateIsGood) {
                    // We've already got a good match, so if this one isn't also good, it's not
                    // worth considering at all.
                    if (!isRefreshRateGoodMatch(candidate.getRefreshRate())) {
                        continue;
                    }

                    if (mayReduceRefreshRate()) {
                        // User asked for the lowest possible refresh rate, so don't raise it if we
                        // have a good match already
                        if (candidate.getRefreshRate() > bestMode.getRefreshRate()) {
                            continue;
                        }
                    }
                    else {
                        // User asked for the highest possible refresh rate, so don't reduce it if we
                        // have a good match already
                        if (refreshRateReduced) {
                            continue;
                        }
                    }
                }
                else if (!isRefreshRateGoodMatch(candidate.getRefreshRate())) {
                    // We didn't have a good match and this match isn't good either, so just don't
                    // reduce the refresh rate.
                    if (refreshRateReduced) {
                        continue;
                    }
                } else {
                    // We didn't have a good match and this match is good. Prefer this refresh rate
                    // even if it reduces the refresh rate. Lowering the refresh rate can be beneficial
                    // when streaming a 60 FPS stream on a 90 Hz device. We want to select 60 Hz to
                    // match the frame rate even if the active display mode is 90 Hz.
                }

                bestMode = candidate;
                refreshRateIsGood = isRefreshRateGoodMatch(candidate.getRefreshRate());
                refreshRateIsEqual = isRefreshRateEqualMatch(candidate.getRefreshRate());
            }

            LimeLog.info("Best display mode: "+bestMode.getPhysicalWidth()+"x"+
                    bestMode.getPhysicalHeight()+"x"+bestMode.getRefreshRate());

            // Only apply new window layout parameters if we've actually changed the display mode
            if (display.getMode().getModeId() != bestMode.getModeId()) {
                // If we only changed refresh rate and we're on an OS that supports Surface.setFrameRate()
                // use that instead of using preferredDisplayModeId to avoid the possibility of triggering
                // bugs that can cause the system to switch from 4K60 to 4K24 on Chromecast 4K.
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S ||
                        display.getMode().getPhysicalWidth() != bestMode.getPhysicalWidth() ||
                        display.getMode().getPhysicalHeight() != bestMode.getPhysicalHeight()) {
                    // Apply the display mode change
                    windowLayoutParams.preferredDisplayModeId = bestMode.getModeId();
                    getWindow().setAttributes(windowLayoutParams);
                }
                else {
                    LimeLog.info("Using setFrameRate() instead of preferredDisplayModeId due to matching resolution");
                }
            }
            else {
                LimeLog.info("Current display mode is already the best display mode");
            }

            displayRefreshRate = bestMode.getRefreshRate();
        }
        // On L, we can at least tell the OS that we want a refresh rate
        else {
            float bestRefreshRate = display.getRefreshRate();
            for (float candidate : display.getSupportedRefreshRates()) {
                LimeLog.info("Examining refresh rate: "+candidate);

                if (candidate > bestRefreshRate) {
                    // Ensure the frame rate stays around 60 Hz for <= 60 FPS streams
                    if (prefConfig.fps <= 60) {
                        if (candidate >= 63) {
                            continue;
                        }
                    }

                    bestRefreshRate = candidate;
                }
            }

            LimeLog.info("Selected refresh rate: "+bestRefreshRate);
            windowLayoutParams.preferredRefreshRate = bestRefreshRate;
            displayRefreshRate = bestRefreshRate;

            // Apply the refresh rate change
            getWindow().setAttributes(windowLayoutParams);
        }

        // Until Marshmallow, we can't ask for a 4K display mode, so we'll
        // need to hint the OS to provide one.
        boolean aspectRatioMatch = false;
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            // We'll calculate whether we need to scale by aspect ratio. If not, we'll use
            // setFixedSize so we can handle 4K properly. The only known devices that have
            // >= 4K screens have exactly 4K screens, so we'll be able to hit this good path
            // on these devices. On Marshmallow, we can start changing to 4K manually but no
            // 4K devices run 6.0 at the moment.
            Point screenSize = new Point(0, 0);
            display.getSize(screenSize);

            double screenAspectRatio = ((double)screenSize.y) / screenSize.x;
            double streamAspectRatio = ((double)prefConfig.height) / prefConfig.width;
            if (Math.abs(screenAspectRatio - streamAspectRatio) < 0.001) {
                LimeLog.info("Stream has compatible aspect ratio with output display");
                aspectRatioMatch = true;
            }
        }

        if (prefConfig.stretchVideo || aspectRatioMatch) {
            // Set the surface to the size of the video
            streamView.getHolder().setFixedSize(prefConfig.width, prefConfig.height);
        }
        else {
            // Set the surface to scale based on the aspect ratio of the stream
            streamView.setDesiredAspectRatio((double)prefConfig.width / (double)prefConfig.height);
        }

        // Set the desired refresh rate that will get passed into setFrameRate() later
        desiredRefreshRate = displayRefreshRate;

        if (getPackageManager().hasSystemFeature(PackageManager.FEATURE_TELEVISION) ||
                getPackageManager().hasSystemFeature(PackageManager.FEATURE_LEANBACK)) {
            // TVs may take a few moments to switch refresh rates, and we can probably assume
            // it will be eventually activated.
            // TODO: Improve this
            return displayRefreshRate;
        }
        else {
            // Use the lower of the current refresh rate and the selected refresh rate.
            // The preferred refresh rate may not actually be applied (ex: Battery Saver mode).
            return Math.min(getWindowManager().getDefaultDisplay().getRefreshRate(), displayRefreshRate);
        }
    }

    @SuppressLint("InlinedApi")
    private final Runnable hideSystemUi = new Runnable() {
            @Override
            public void run() {
                // TODO: Do we want to use WindowInsetsController here on R+ instead of
                // SYSTEM_UI_FLAG_IMMERSIVE_STICKY? They seem to do the same thing as of S...

                // In multi-window mode on N+, we need to drop our layout flags or we'll
                // be drawing underneath the system UI.
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isInMultiWindowMode()) {
                    Game.this.getWindow().getDecorView().setSystemUiVisibility(
                            View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
                }
                else {
                    // Use immersive mode
                    Game.this.getWindow().getDecorView().setSystemUiVisibility(
                            View.SYSTEM_UI_FLAG_LAYOUT_STABLE |
                            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
                            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
                            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
                            View.SYSTEM_UI_FLAG_FULLSCREEN |
                            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
                }
            }
    };

    private void hideSystemUi(int delay) {
        Handler h = getWindow().getDecorView().getHandler();
        if (h != null) {
            h.removeCallbacks(hideSystemUi);
            h.postDelayed(hideSystemUi, delay);
        }
    }

    @Override
    @TargetApi(Build.VERSION_CODES.N)
    public void onMultiWindowModeChanged(boolean isInMultiWindowMode) {
        super.onMultiWindowModeChanged(isInMultiWindowMode);

        // In multi-window, we don't want to use the full-screen layout
        // flag. It will cause us to collide with the system UI.
        // This function will also be called for PiP so we can cover
        // that case here too.
        if (isInMultiWindowMode) {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            decoderRenderer.notifyVideoBackground();
        }
        else {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
            decoderRenderer.notifyVideoForeground();
        }

        // Correct the system UI visibility flags
        hideSystemUi(50);
    }

    @Override
    @TargetApi(Build.VERSION_CODES.O)
    public void onPictureInPictureModeChanged(boolean isInPictureInPictureMode,
                                              android.content.res.Configuration newConfig) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig);
        updateInputHints();
        if (isInPictureInPictureMode) {
            stopRemoteMicrophone();
            startBackgroundStreamService();
        }
    }

    @Override
    protected void onDestroy() {
        dismissTvReconnect();
        if (streamView != null) streamView.removeCallbacks(panFrame);
        panFrameTime = 0;
        if (inputHints != null) { inputHints.close(); inputHints = null; }
        stopRemoteMicrophone();
        autoBitrateHandler.removeCallbacks(autoBitrateStableSample);
        autoBitrateHandler.removeCallbacks(contentAdaptiveControlRetry);
        reconnectHandler.removeCallbacksAndMessages(null);

        if (keyboardLayoutRoot != null && keyboardLayoutListener != null) {
            keyboardLayoutRoot.getViewTreeObserver()
                    .removeOnGlobalLayoutListener(keyboardLayoutListener);
        }

        if (KeyInterceptorService.instance != null) {
            KeyInterceptorService.instance.updateKeyFiltering(false);
        }

        if (activeInstance == this) {
            activeInstance = null;
        }

        super.onDestroy();

        if (controllerHandler != null) {
            controllerHandler.destroy();
        }
        if (keyboardTranslator != null) {
            InputManager inputManager = (InputManager) getSystemService(Context.INPUT_SERVICE);
            inputManager.unregisterInputDeviceListener(keyboardTranslator);
        }

        if (lowLatencyWifiLock != null) {
            lowLatencyWifiLock.release();
        }
        if (highPerfWifiLock != null) {
            highPerfWifiLock.release();
        }

        if (connectedToUsbDriverService) {
            // Unbind from the discovery service
            unbindService(usbDriverServiceConnection);
        }

        // Destroy the capture provider
        cursorHandler.removeCallbacksAndMessages(null);
        inputCaptureProvider.destroy();
        if (replacementStreamIntent != null) {
            Intent replacement = replacementStreamIntent;
            android.content.Context appContext = getApplicationContext();
            new Handler(Looper.getMainLooper()).post(() -> appContext.startActivity(replacement));
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        activityResumed = true;
        updateInputHints();
        startGrantedMicrophone();
        updateMicControls();
        deliverDictation();
        activeInstance = this;
        backgroundTransitionRequested = false;
        streamPageStopped = false;
        stopBackgroundStreamService();

        if (prefConfig.keyboardInterceptor && !KeyInterceptorService.isServiceRunning) {
            Toast.makeText(this, "Warning: Keyboard Interceptor is enabled in settings, but the Accessibility Service is not running. Please toggle it off/on in system Accessibility Settings.", Toast.LENGTH_LONG).show();
        }

        // Enable key filtering when stream begins
        if (prefConfig.keyboardInterceptor && KeyInterceptorService.instance != null) {
            syncKeyboardOwner();
        }
    }

    @Override
    protected void onPause() {
        activityResumed = false;
        updateInputHints();
        if (remoteMic != null) stopRemoteMicrophone();
        else updateMicControls();
        releaseForwardedKeys();
        syncKeyboardOwner();
        if (streamView != null) streamView.invalidateInputSession();
        if (isFinishing()) {
            if (activeInstance == this) {
                activeInstance = null;
            }

            // Stop any further input device notifications before we lose focus (and pointer capture)
            if (controllerHandler != null) {
                controllerHandler.stop();
            }

            // Ungrab input to prevent further input device notifications
            setInputGrabState(false);
        }

        super.onPause();
    }

    @Override
    protected void onStop() {
        stopRemoteMicrophone();
        if (voiceInputInFlight && (connecting || connected) && !leavingStream && !isFinishing()) {
            // System speech recognizer opens over the stream Activity without PiP. Keep the
            // session and connection alive so the dictated result can be delivered on return.
            super.onStop();
            return;
        }
        pendingDictation = null;
        com.limelight.piulink.MonitorSelection.cancel(this);
        com.limelight.piulink.MonitorSelection.release(this, getIntent().getStringExtra(EXTRA_HOST),
                getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION));
        getIntent().removeExtra(EXTRA_DISPLAY_PREPARATION);
        if (backgroundSession != null) stopBackgroundStreamService();
        streamPageStopped = true;
        dismissTvReconnect();
        displayedFailureDialog = true;
        reconnectHandler.removeCallbacksAndMessages(null);
        autoBitrateHandler.removeCallbacks(autoBitrateStableSample);

        if (KeyInterceptorService.instance != null) {
            KeyInterceptorService.instance.updateKeyFiltering(false);
        }

        super.onStop();

        SpinnerDialog.closeDialogs(this);
        Dialog.closeDialogs();

        if (virtualController != null) {
            virtualController.hide();
        }

        if (conn != null) {
            int videoFormat = decoderRenderer.getActiveVideoFormat();

            displayedFailureDialog = true;
            stopConnection(null, !television);

            if (prefConfig.enableLatencyToast) {
                int averageEndToEndLat = decoderRenderer.getAverageEndToEndLatency();
                int averageDecoderLat = decoderRenderer.getAverageDecoderLatency();
                String message = null;
                if (averageEndToEndLat > 0) {
                    message = getResources().getString(R.string.conn_client_latency)+" "+averageEndToEndLat+" ms";
                    if (averageDecoderLat > 0) {
                        message += " ("+getResources().getString(R.string.conn_client_latency_hw)+" "+averageDecoderLat+" ms)";
                    }
                }
                else if (averageDecoderLat > 0) {
                    message = getResources().getString(R.string.conn_hardware_latency)+" "+averageDecoderLat+" ms";
                }

                // Add the video codec to the post-stream toast
                if (message != null) {
                    message += " [";

                    if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_H264) != 0) {
                        message += "H.264";
                    }
                    else if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_H265) != 0) {
                        message += "HEVC";
                    }
                    else if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_AV1) != 0) {
                        message += "AV1";
                    }
                    else {
                        message += "UNKNOWN";
                    }

                    if ((videoFormat & MoonBridge.VIDEO_FORMAT_MASK_10BIT) != 0) {
                        message += " HDR";
                    }

                    message += "]";
                }

                if (message != null) {
                    Toast.makeText(this, message, Toast.LENGTH_LONG).show();
                }
            }

            // Clear the tombstone count if we terminated normally
            if (!reportedCrash && tombstonePrefs.getInt("CrashCount", 0) != 0) {
                tombstonePrefs.edit()
                        .putInt("CrashCount", 0)
                        .putInt("LastNotifiedCrashCount", 0)
                        .apply();
            }
        }

        finish();
    }

    private void setInputGrabState(boolean grab) {
        mouseInputStats.reset();
        touchInputStats.reset();
        relativeMouseMotion.reset();
        if (!grab) {
            releaseForwardedKeys();
            releaseActiveShortcutRailChord();
        }

        // Grab/ungrab the mouse cursor
        if (grab) {
            inputCaptureProvider.enableCapture();

            // Enabling capture may hide the cursor again, so
            // we will need to show it again.
            if (cursorVisible) {
                inputCaptureProvider.showCursor();
            }
        }
        else {
            inputCaptureProvider.disableCapture();
        }

        // Grab/ungrab system keyboard shortcuts
        setMetaKeyCaptureState(grab);

        grabbedInput = grab;
        syncKeyboardOwner();
    }

    private final Runnable toggleGrab = new Runnable() {
        @Override
        public void run() {
            setInputGrabState(!grabbedInput);
        }
    };

    // Returns true if the key stroke was consumed
    private boolean handleSpecialKeys(int androidKeyCode, boolean down) {
        int modifierMask = 0;
        int nonModifierKeyCode = KeyEvent.KEYCODE_UNKNOWN;

        if (androidKeyCode == KeyEvent.KEYCODE_CTRL_LEFT ||
            androidKeyCode == KeyEvent.KEYCODE_CTRL_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_CTRL;
        }
        else if (androidKeyCode == KeyEvent.KEYCODE_SHIFT_LEFT ||
                 androidKeyCode == KeyEvent.KEYCODE_SHIFT_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_SHIFT;
        }
        else if (androidKeyCode == KeyEvent.KEYCODE_ALT_LEFT ||
                 androidKeyCode == KeyEvent.KEYCODE_ALT_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_ALT;
        }
        else if (androidKeyCode == KeyEvent.KEYCODE_META_LEFT ||
                androidKeyCode == KeyEvent.KEYCODE_META_RIGHT) {
            modifierMask = KeyboardPacket.MODIFIER_META;
        }
        else {
            nonModifierKeyCode = androidKeyCode;
        }

        if (down) {
            this.modifierFlags |= modifierMask;
        }
        else {
            this.modifierFlags &= ~modifierMask;
        }

        // Handle the special combos on the key up
        if (waitingForAllModifiersUp || specialKeyCode != KeyEvent.KEYCODE_UNKNOWN) {
            if (specialKeyCode == androidKeyCode) {
                // If this is a key up for the special key itself, eat that because the host never saw the original key down
                return true;
            }
            else if (modifierFlags != 0) {
                // While we're waiting for modifiers to come up, eat all key downs and allow all key ups to pass
                return down;
            }
            else {
                // When all modifiers are up, perform the special action
                switch (specialKeyCode) {
                    // Toggle input grab
                    case KeyEvent.KEYCODE_Z:
                        Handler h = getWindow().getDecorView().getHandler();
                        if (h != null) {
                            h.postDelayed(toggleGrab, 250);
                        }
                        break;

                    // Quit
                    case KeyEvent.KEYCODE_Q:
                        finish();
                        break;

                    case KeyEvent.KEYCODE_M:
                        runOnUiThread(() -> openCapturePicker(true, 0, 0));
                        break;

                    // Toggle cursor visibility
                    case KeyEvent.KEYCODE_C:
                        if (!grabbedInput) {
                            inputCaptureProvider.enableCapture();
                            grabbedInput = true;
                        }
                        cursorVisible = !cursorVisible;
                        if (cursorVisible) {
                            inputCaptureProvider.showCursor();
                        } else {
                            inputCaptureProvider.hideCursor();
                        }
                        break;

                    default:
                        break;
                }

                // Reset special key state
                specialKeyCode = KeyEvent.KEYCODE_UNKNOWN;
                waitingForAllModifiersUp = false;
            }
        }
        // Check if Ctrl+Alt+Shift is down when a non-modifier key is pressed
        else if ((modifierFlags & (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT | KeyboardPacket.MODIFIER_SHIFT)) ==
                (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT | KeyboardPacket.MODIFIER_SHIFT) &&
                (down && nonModifierKeyCode != KeyEvent.KEYCODE_UNKNOWN)) {
            switch (androidKeyCode) {
                case KeyEvent.KEYCODE_Z:
                case KeyEvent.KEYCODE_Q:
                case KeyEvent.KEYCODE_C:
                case KeyEvent.KEYCODE_M:
                    // Remember that a special key combo was activated, so we can consume all key
                    // events until the modifiers come up
                    specialKeyCode = androidKeyCode;
                    waitingForAllModifiersUp = true;
                    return true;

                default:
                    // This isn't a special combo that we consume on the client side
                    return false;
            }
        }

        // Not a special combo
        return false;
    }

    // We cannot simply use modifierFlags for all key event processing, because
    // some IMEs will not generate real key events for pressing Shift. Instead
    // they will simply send key events with isShiftPressed() returning true,
    // and we will need to send the modifier flag ourselves.
    private byte getModifierState(KeyEvent event) {
        // Start with the global modifier state to ensure we cover the case
        // detailed in https://github.com/moonlight-stream/moonlight-android/issues/840
        byte modifier = getModifierState();
        if (event.isShiftPressed()) {
            modifier |= KeyboardPacket.MODIFIER_SHIFT;
        }
        if (event.isCtrlPressed()) {
            modifier |= KeyboardPacket.MODIFIER_CTRL;
        }
        if (event.isAltPressed()) {
            modifier |= KeyboardPacket.MODIFIER_ALT;
        }
        if (event.isMetaPressed()) {
            modifier |= KeyboardPacket.MODIFIER_META;
        }
        return modifier;
    }

    private byte getModifierState() {
        return (byte) modifierFlags;
    }

    public boolean isInputGrabbed() {
        return grabbedInput;
    }

    public boolean isSessionActive() {
        return connected && conn != null && grabbedInput && activityResumed && hasWindowFocus()
                && !streamMenuOpen && !shortcutRailOpen && !leavingStream
                && !(Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isInPictureInPictureMode());
    }

    private final com.limelight.piulink.RemoteKeyboardState forwardedKeys = new com.limelight.piulink.RemoteKeyboardState();

    private synchronized void sendTrackedKeyboardInput(short key, byte direction, byte modifiers, byte flags) {
        if (conn == null || !connected) return;
        if (!forwardedKeys.accept(key, direction == KeyboardPacket.KEY_DOWN, flags)) return;
        conn.sendKeyboardInput(key, direction, modifiers, flags);
    }

    private synchronized void releaseForwardedKeys() {
        java.util.Map<Short, Byte> releases = forwardedKeys.releaseAll();
        if (conn != null && connected) {
            for (java.util.Map.Entry<Short, Byte> key : releases.entrySet())
                conn.sendKeyboardInput(key.getKey(), KeyboardPacket.KEY_UP, (byte) 0, key.getValue());
        }
        modifierFlags = 0;
    }

    private void syncKeyboardOwner() {
        if (KeyInterceptorService.instance != null)
            KeyInterceptorService.instance.updateKeyFiltering(prefConfig != null &&
                    prefConfig.keyboardInterceptor && isSessionActive());
    }

    private boolean canSendRemoteText() {
        return conn != null && connected && activityResumed && hasWindowFocus()
                && !streamMenuOpen && !shortcutRailOpen && !leavingStream
                && !(Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && isInPictureInPictureMode());
    }

    public boolean handleAccessibilityKeyEvent(KeyEvent event) {
        if (!isSessionActive()) return false;
        if (handleTvMenuKey(event)) return true;
        if (event.getAction() == KeyEvent.ACTION_DOWN) {
            return handleKeyDown(event);
        } else if (event.getAction() == KeyEvent.ACTION_UP) {
            return handleKeyUp(event);
        }
        return false;
    }

    private boolean isKeyboardKey(int keyCode) {
        // Exclude gamepad buttons
        if (KeyEvent.isGamepadButton(keyCode)) return false;
        if (keyCode >= KeyEvent.KEYCODE_DPAD_UP && keyCode <= KeyEvent.KEYCODE_DPAD_CENTER) return false;
        
        // Exclude system keys that the service does not consume
        if (keyCode == KeyEvent.KEYCODE_POWER ||
            keyCode == KeyEvent.KEYCODE_VOLUME_UP ||
            keyCode == KeyEvent.KEYCODE_VOLUME_DOWN ||
            keyCode == KeyEvent.KEYCODE_VOLUME_MUTE ||
            keyCode == KeyEvent.KEYCODE_HOME ||
            keyCode == KeyEvent.KEYCODE_BACK) {
            return false;
        }
        
        return true;
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        int keyCode = event.getKeyCode();
        if (handleTvMenuKey(event)) return true;
        if (television && dispatchTvStreamKey(event)) return true;
        // Some Android TV controller mappings expose the physical east button by its Linux
        // scan code but rewrite or omit KEYCODE_BUTTON_B. Accept both representations.
        boolean gamepadBack = television ? TvStreamKeys.east(keyCode, event.getScanCode(), inputSources(event))
                : keyCode == KeyEvent.KEYCODE_BUTTON_B || event.getScanCode() == 305;
        if (gamepadBack) {
            LimeLog.info("Stream Activity received gamepad Back/B: key=" + keyCode +
                    " scan=" + event.getScanCode() + " action=" + event.getAction() +
                    " device=" + event.getDeviceId() + " source=" + event.getSource() +
                    " menuOpen=" + streamMenuOpen + " railOpen=" + shortcutRailOpen +
                    " leaving=" + leavingStream);
        }
        if (!television && keyCode == KeyEvent.KEYCODE_BACK &&
                SystemClock.uptimeMillis() < suppressOverlayFallbackUntilMs) {
            return true;
        }
        if (!television && keyCode == KeyEvent.KEYCODE_DPAD_CENTER &&
                SystemClock.uptimeMillis() < suppressOverlayFallbackUntilMs) {
            return true;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) {
            LimeLog.info("Stream Activity received DPAD: key=" + keyCode +
                    " action=" + event.getAction() + " device=" + event.getDeviceId() +
                    " source=" + event.getSource() + " railOpen=" + shortcutRailOpen);
        }

        if ((streamMenuOpen || shortcutRailOpen) &&
                (keyCode == KeyEvent.KEYCODE_DPAD_UP ||
                        keyCode == KeyEvent.KEYCODE_DPAD_DOWN)) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                moveOverlayFocus(keyCode == KeyEvent.KEYCODE_DPAD_DOWN ? 1 : -1);
            }
            return true;
        }

        if ((streamMenuOpen || shortcutRailOpen) &&
                (keyCode == KeyEvent.KEYCODE_BUTTON_A ||
                        keyCode == KeyEvent.KEYCODE_DPAD_CENTER ||
                        keyCode == KeyEvent.KEYCODE_ENTER ||
                        keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER)) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                View focused = getCurrentFocus();
                if (focused != null) {
                    focused.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                    focused.performClick();
                    LimeLog.info("Activated overlay focus with key=" + keyCode);
                }
                if (keyCode == KeyEvent.KEYCODE_BUTTON_A) {
                    suppressOverlayFallbackUntilMs = SystemClock.uptimeMillis() + 750;
                }
            }
            return true;
        }

        if (streamMenuOpen && (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT ||
                keyCode == KeyEvent.KEYCODE_BACK || gamepadBack)) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                if (gamepadBack) {
                    suppressOverlayFallbackUntilMs = SystemClock.uptimeMillis() + 750;
                }
                setStreamMenuOpen(false, true);
                streamView.requestFocus();
                LimeLog.info("Closed stream menu panel with key=" + keyCode);
            }
            return true;
        }

        if (shortcutRailOpen && (keyCode == KeyEvent.KEYCODE_DPAD_LEFT ||
                keyCode == KeyEvent.KEYCODE_BACK || gamepadBack)) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                if (gamepadBack) {
                    suppressOverlayFallbackUntilMs = SystemClock.uptimeMillis() + 750;
                }
                releaseActiveShortcutRailChord();
                setShortcutRailOpen(false, true);
                streamView.requestFocus();
                LimeLog.info("Closed shortcut rail with key=" + keyCode);
            }
            return true;
        }

        if (!television && !streamMenuOpen && !shortcutRailOpen && gamepadBack) {
            if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
                suppressOverlayFallbackUntilMs = SystemClock.uptimeMillis() + 750;
                onBackPressed();
            }
            return true;
        }

        // Focus is not guaranteed to remain on StreamView on Android TV (SurfaceView and
        // vendor input-method implementations frequently move it). Reserve left/right at
        // the Activity boundary for TV remotes only. A gamepad D-pad belongs to the streamed
        // game and must never open an edge overlay.
        if (!shortcutRailOpen && !streamMenuOpen &&
                handleStreamOverlayNavigation(keyCode, event)) {
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        // A consumed accessibility event never reaches this callback. Events delivered here
        // belong to the Activity, including when filtering was refused or disconnected.
        return handleKeyDown(event) || super.onKeyDown(keyCode, event);
    }

    @Override
    public boolean handleKeyDown(KeyEvent event) {
        if (!canSendRemoteText()) return false;
        // Pass-through virtual navigation keys
        if ((event.getFlags() & KeyEvent.FLAG_VIRTUAL_HARD_KEY) != 0) {
            return false;
        }

        // Handle a synthetic back button event that some Android OS versions
        // create as a result of a right-click. This event WILL repeat if
        // the right mouse button is held down, so we ignore those.
        int eventSource = event.getSource();
        if ((eventSource == InputDevice.SOURCE_MOUSE ||
                eventSource == InputDevice.SOURCE_MOUSE_RELATIVE) &&
                event.getKeyCode() == KeyEvent.KEYCODE_BACK) {

            // Send the right mouse button event if mouse back and forward
            // are disabled. If they are enabled, handleMotionEvent() will take
            // care of this.
            if (!prefConfig.mouseNavButtons && conn != null) {
                conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
            }

            // Always return true, otherwise the back press will be propagated
            // up to the parent and finish the activity.
            return true;
        }

        boolean handled = false;

        if (ControllerHandler.isGameControllerDevice(event.getDevice())) {
            // Always try the controller handler first, unless it's an alphanumeric keyboard device.
            // Otherwise, controller handler will eat keyboard d-pad events.
            handled = controllerHandler.handleButtonDown(event);
        }

        // Try the keyboard handler if it wasn't handled as a game controller
        if (!handled) {
            // Let this method take duplicate key down events
            if (handleSpecialKeys(event.getKeyCode(), true)) {
                return true;
            }

            // Pass through keyboard input if we're not grabbing
            if (!grabbedInput) {
                return false;
            }

            // We'll send it as a raw key event if we have a key mapping, otherwise we'll send it
            // as UTF-8 text (if it's a printable character).
            short translated = keyboardTranslator.translate(event.getKeyCode(), event.getDeviceId());
            if (translated == 0) {
                // Make sure it has a valid Unicode representation and it's not a dead character
                // (which we don't support). If those are true, we can send it as UTF-8 text.
                //
                // NB: We need to be sure this happens before the getRepeatCount() check because
                // UTF-8 events don't auto-repeat on the host side.
                int unicodeChar = event.getUnicodeChar();
                if ((unicodeChar & KeyCharacterMap.COMBINING_ACCENT) == 0 && (unicodeChar & KeyCharacterMap.COMBINING_ACCENT_MASK) != 0) {
                    if (conn != null) {
                        if (canSendRemoteText() && Character.isValidCodePoint(unicodeChar))
                            conn.sendUtf8Text(new String(Character.toChars(unicodeChar)));
                    }
                    return true;
                }

                return false;
            }

            // Android generates repeated ACTION_DOWN events while a hardware key is held.
            // The host handles normal repeats itself, but Alt+Tab is special: repeating Tab
            // while Alt remains down is what advances the Windows task switcher.
            if (event.getRepeatCount() > 0) {
                byte modifiers = getModifierState(event);
                if (event.getKeyCode() == KeyEvent.KEYCODE_TAB &&
                        (modifiers & KeyboardPacket.MODIFIER_ALT) != 0 &&
                        conn != null) {
                    sendTrackedKeyboardInput(translated, KeyboardPacket.KEY_UP, modifiers,
                            keyboardTranslator.hasNormalizedMapping(event.getKeyCode(), event.getDeviceId()) ? 0 : MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
                    sendTrackedKeyboardInput(translated, KeyboardPacket.KEY_DOWN, modifiers,
                            keyboardTranslator.hasNormalizedMapping(event.getKeyCode(), event.getDeviceId()) ? 0 : MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
                }
                return true;
            }

            if (conn != null) {
                sendTrackedKeyboardInput(translated, KeyboardPacket.KEY_DOWN, getModifierState(event),
                        keyboardTranslator.hasNormalizedMapping(event.getKeyCode(), event.getDeviceId()) ? 0 : MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
            }
        }

        return true;
    }

    @Override
    public boolean onKeyUp(int keyCode, KeyEvent event) {
        // See onKeyDown: never infer ownership from service process liveness.
        return handleKeyUp(event) || super.onKeyUp(keyCode, event);
    }

    @Override
    public boolean handleKeyUp(KeyEvent event) {
        if (!canSendRemoteText()) return false;
        // Pass-through virtual navigation keys
        if ((event.getFlags() & KeyEvent.FLAG_VIRTUAL_HARD_KEY) != 0) {
            return false;
        }

        // Handle a synthetic back button event that some Android OS versions
        // create as a result of a right-click.
        int eventSource = event.getSource();
        if ((eventSource == InputDevice.SOURCE_MOUSE ||
                eventSource == InputDevice.SOURCE_MOUSE_RELATIVE) &&
                event.getKeyCode() == KeyEvent.KEYCODE_BACK) {

            // Send the right mouse button event if mouse back and forward
            // are disabled. If they are enabled, handleMotionEvent() will take
            // care of this.
            if (!prefConfig.mouseNavButtons) {
                conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
            }

            // Always return true, otherwise the back press will be propagated
            // up to the parent and finish the activity.
            return true;
        }

        boolean handled = false;
        if (ControllerHandler.isGameControllerDevice(event.getDevice())) {
            // Always try the controller handler first, unless it's an alphanumeric keyboard device.
            // Otherwise, controller handler will eat keyboard d-pad events.
            handled = controllerHandler.handleButtonUp(event);
        }

        // Try the keyboard handler if it wasn't handled as a game controller
        if (!handled) {
            if (handleSpecialKeys(event.getKeyCode(), false)) {
                return true;
            }

            // Pass through keyboard input if we're not grabbing
            if (!grabbedInput) {
                return false;
            }

            short translated = keyboardTranslator.translate(event.getKeyCode(), event.getDeviceId());
            if (translated == 0) {
                // If we sent this event as UTF-8 on key down, also report that it was handled
                // when we get the key up event for it.
                int unicodeChar = event.getUnicodeChar();
                return (unicodeChar & KeyCharacterMap.COMBINING_ACCENT) == 0 && (unicodeChar & KeyCharacterMap.COMBINING_ACCENT_MASK) != 0;
            }

            if (conn != null) {
                sendTrackedKeyboardInput(translated, KeyboardPacket.KEY_UP, getModifierState(event),
                        keyboardTranslator.hasNormalizedMapping(event.getKeyCode(), event.getDeviceId()) ? 0 : MoonBridge.SS_KBE_FLAG_NON_NORMALIZED);
            }
        }

        return true;
    }

    @Override
    public boolean onKeyMultiple(int keyCode, int repeatCount, KeyEvent event) {
        return handleKeyMultiple(event) || super.onKeyMultiple(keyCode, repeatCount, event);
    }

    private boolean handleKeyMultiple(KeyEvent event) {
        if (!canSendRemoteText()) return false;
        // We can receive keys from a software keyboard that don't correspond to any existing
        // KEYCODE value. Android will give those to us as an ACTION_MULTIPLE KeyEvent.
        //
        // Despite the fact that the Android docs say this is unused since API level 29, these
        // events are still sent as of Android 13 for the above case.
        //
        // For other cases of ACTION_MULTIPLE, we will not report those as handled so hopefully
        // they will be passed to us again as regular singular key events.
        if (event.getKeyCode() != KeyEvent.KEYCODE_UNKNOWN || event.getCharacters() == null) {
            return false;
        }

        conn.sendUtf8Text(event.getCharacters());
        return true;
    }

    @Override
    public void handleTextInput(CharSequence text) {
        if (canSendRemoteText() && text != null) conn.sendUtf8Text(text.toString());
    }

    private void startVoiceInput() {
        if (conn == null) {
            Toast.makeText(this, R.string.voice_input_stream_not_ready, Toast.LENGTH_SHORT).show();
            return;
        }

        // Dictation returns text from the system recognizer. Live meeting audio has a separate control.
        voiceInputCapturePaused = grabbedInput && !softwareKeyboardVisible;
        if (voiceInputCapturePaused) {
            inputCaptureProvider.disableCapture();
        }

        Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL,
                RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, Locale.getDefault().toLanguageTag());
        intent.putExtra(RecognizerIntent.EXTRA_PROMPT, getString(R.string.voice_input_prompt));
        intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1);
        voiceInputInFlight = true;
        suppressPipRefCount++;
        updatePipAutoEnter();
        try {
            startActivityForResult(intent, VOICE_INPUT_REQUEST);
        }
        catch (android.content.ActivityNotFoundException e) {
            voiceInputInFlight = false;
            suppressPipRefCount = Math.max(0, suppressPipRefCount - 1);
            updatePipAutoEnter();
            restoreVoiceInputCapture();
            Toast.makeText(this, R.string.voice_input_unavailable, Toast.LENGTH_LONG).show();
        }
    }

    private void deliverDictation() {
        if (pendingDictation == null || !canSendRemoteText()) return;
        String text = pendingDictation;
        pendingDictation = null;
        conn.sendUtf8Text(text);
        Toast.makeText(this, getString(R.string.voice_input_sent, text), Toast.LENGTH_SHORT).show();
    }

    private void restoreVoiceInputCapture() {
        if (voiceInputCapturePaused && grabbedInput && !softwareKeyboardVisible) {
            inputCaptureProvider.enableCapture();
        }
        voiceInputCapturePaused = false;
    }

    @Override
    @SuppressWarnings("deprecation")
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != VOICE_INPUT_REQUEST) {
            return;
        }

        try {
            if (resultCode != RESULT_OK || data == null) {
                return;
            }

            ArrayList<String> results = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
            if (results == null || results.isEmpty()) {
                Toast.makeText(this, R.string.voice_input_no_result, Toast.LENGTH_SHORT).show();
                return;
            }

            String text = results.get(0).trim();
            if (!text.isEmpty() && conn != null && connected) {
                // Use the same UTF-8 path as an IME commit, so Chinese text and punctuation are
                // delivered to the active field on the Windows desktop without keycode guessing.
                pendingDictation = text;
                deliverDictation();
            }
            else if (text.isEmpty()) {
                Toast.makeText(this, R.string.voice_input_no_result, Toast.LENGTH_SHORT).show();
            }
            else {
                Toast.makeText(this, R.string.voice_input_stream_not_ready, Toast.LENGTH_SHORT).show();
            }
        }
        finally {
            if (voiceInputInFlight) {
                voiceInputInFlight = false;
                suppressPipRefCount = Math.max(0, suppressPipRefCount - 1);
                updatePipAutoEnter();
            }
            restoreVoiceInputCapture();
        }
    }

    private TouchContext getTouchContext(int actionIndex)
    {
        if (actionIndex < touchContextMap.length) {
            return touchContextMap[actionIndex];
        }
        else {
            return null;
        }
    }

    private void onRemoteTap() { if (inputHints != null) inputHints.tapped(); }
    private void updateInputHints() {
        if (inputHints != null) inputHints.setRunning(connected && activityResumed && !leavingStream && !isInPictureInPictureMode());
    }
    private boolean canAutoShowKeyboard() {
        if (!prefConfig.autoTextKeyboard || !canSendRemoteText() || softwareKeyboardVisible) return false;
        for (int id : InputDevice.getDeviceIds()) {
            InputDevice device = InputDevice.getDevice(id);
            if (device != null && !device.isVirtual() && device.getKeyboardType() == InputDevice.KEYBOARD_TYPE_ALPHABETIC &&
                    device.supportsSource(InputDevice.SOURCE_KEYBOARD) &&
                    (Build.VERSION.SDK_INT < 29 || device.isExternal())) return false;
        }
        return true;
    }

    @Override
    public void toggleKeyboard() {
        if (inputHints != null) inputHints.cancelTap();
        openSoftwareKeyboard(false);
    }

    private void openSoftwareKeyboard(boolean automatic) {
        // Both edge menus and the gesture use this entry point. A visible rail owns
        // input and canSendRemoteText() rejects IME commits until it has closed.
        releaseActiveShortcutRailChord();
        setStreamMenuOpen(false, false);
        setShortcutRailOpen(false, false);
        updateSoftwareKeyboardState();
        if (automatic && (softwareKeyboardVisible || !canAutoShowKeyboard())) return;
        LimeLog.info(softwareKeyboardVisible ? "Hiding software keyboard" : "Showing software keyboard");
        // Release pointer capture before opening the IME. On some Android 16 devices, the
        // first IME window is visible but cannot receive touch while capture is still active.
        inputCaptureProvider.disableCapture();

        InputMethodManager inputManager =
                (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
        if (softwareKeyboardVisible) {
            inputManager.hideSoftInputFromWindow(streamView.getWindowToken(), 0);
            streamView.setTextInputEnabled(false);
            return;
        }

        // StreamView exposes an InputConnection that forwards committed IME text to the PC.
        // Explicitly restart and show the IME instead of toggleSoftInput(), whose behavior is
        // inconsistent when an overlay button temporarily owned focus.
        streamView.setTextInputEnabled(true);
        streamView.requestFocus();
        inputManager.restartInput(streamView);
        streamView.postDelayed(() -> {
            if (!canSendRemoteText() || !streamView.onCheckIsTextEditor()) return;
            if (automatic && !canAutoShowKeyboard()) {
                if (!softwareKeyboardVisible) {
                    streamView.setTextInputEnabled(false);
                    if (grabbedInput) inputCaptureProvider.enableCapture();
                }
                return;
            }
            streamView.requestFocus();
            inputManager.showSoftInput(streamView, InputMethodManager.SHOW_IMPLICIT);
        }, 100);
    }

    private void setupSoftwareKeyboardTracking() {
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING |
                WindowManager.LayoutParams.SOFT_INPUT_STATE_ALWAYS_HIDDEN);
        keyboardLayoutRoot = getWindow().getDecorView();
        keyboardLayoutListener = this::updateSoftwareKeyboardState;
        keyboardLayoutRoot.getViewTreeObserver()
                .addOnGlobalLayoutListener(keyboardLayoutListener);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            streamView.setOnApplyWindowInsetsListener((view, insets) -> {
                view.post(this::updateSoftwareKeyboardState);
                return view.onApplyWindowInsets(insets);
            });
        }
    }

    private void updateSoftwareKeyboardState() {
        if (keyboardLayoutRoot == null || streamView == null || keyboardLayoutRoot.getHeight() == 0) {
            return;
        }

        Rect visibleFrame = new Rect();
        keyboardLayoutRoot.getWindowVisibleDisplayFrame(visibleFrame);
        int obscuredHeight = keyboardLayoutRoot.getHeight() - visibleFrame.bottom;
        boolean nowVisible = obscuredHeight > keyboardLayoutRoot.getHeight() * 0.18f;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            android.view.WindowInsets insets = keyboardLayoutRoot.getRootWindowInsets();
            if (insets != null) {
                int ime = android.view.WindowInsets.Type.ime();
                nowVisible = insets.isVisible(ime);
                int inset = insets.getInsets(ime).bottom;
                if (nowVisible && inset > 0) {
                    int[] location = new int[2];
                    keyboardLayoutRoot.getLocationOnScreen(location);
                    visibleFrame.bottom = Math.min(visibleFrame.bottom, location[1] + keyboardLayoutRoot.getHeight() - inset);
                }
            }
        }
        boolean changed = nowVisible != softwareKeyboardVisible;
        softwareKeyboardVisible = nowVisible;
        View parent = (View)streamView.getParent();
        int[] origin = new int[2];
        parent.getLocationOnScreen(origin);
        int left = -streamView.getLeft(), top = -streamView.getTop();
        int bottom = parent.getHeight() + top;
        if (nowVisible) bottom = Math.min(bottom, visibleFrame.bottom - origin[1] + top);
        viewport.layout(streamView.getWidth(), streamView.getHeight(), left, top,
                parent.getWidth() + left, bottom, nowVisible);
        applyViewport();
        if (!changed) return;
        if (!nowVisible) {
            streamView.setTextInputEnabled(false);
            if (grabbedInput) {
                streamView.postDelayed(() -> {
                    if (grabbedInput && !softwareKeyboardVisible && activityResumed && !leavingStream) inputCaptureProvider.enableCapture();
                }, 150);
            }
        }
        getWindow().getDecorView().post(this::applyResponsiveStreamOverlayLayout);
    }

    private void applyResponsiveStreamOverlayLayout() {
        if (streamMenuPanel == null || shortcutRail == null) return;
        View decor = getWindow().getDecorView();
        int windowWidth = decor.getWidth();
        int windowHeight = decor.getHeight();
        if (windowWidth <= 0 || windowHeight <= 0) return;

        if (performanceOverlayView != null) {
            // Anchor to the window, not the video frame. A wide, content-height
            // panel keeps the compact rows readable on phones and tablets.
            FrameLayout.LayoutParams statsParams = (FrameLayout.LayoutParams)
                    performanceOverlayView.getLayoutParams();
            statsParams.gravity = android.view.Gravity.TOP | android.view.Gravity.LEFT;
            statsParams.leftMargin = dpToPixels(12);
            statsParams.topMargin = dpToPixels(12);
            statsParams.width = Math.max(dpToPixels(240), windowWidth - dpToPixels(24));
            statsParams.height = FrameLayout.LayoutParams.WRAP_CONTENT;
            performanceOverlayView.setLayoutParams(statsParams);
        }

        int leftWidth = clampPixels(Math.round(windowWidth * 0.30f),
                dpToPixels(240), dpToPixels(360));
        int rightWidth = clampPixels(Math.round(windowWidth * 0.18f),
                dpToPixels(150), dpToPixels(210));
        int verticalMargin = clampPixels(Math.round(windowHeight * 0.02f),
                dpToPixels(6), dpToPixels(24));

        FrameLayout.LayoutParams leftParams = (FrameLayout.LayoutParams)
                streamMenuPanel.getLayoutParams();
        leftParams.width = leftWidth;
        if (UiHelper.isTouchscreenClient(this)) {
            // Fit the actions on tablets, keeping the existing scroll viewport on short screens.
            int menuHeight = dpToPixels(105 + 56 * streamMenuItems.getChildCount());
            leftParams.height = Math.min(windowHeight - 2 * verticalMargin, menuHeight);
        }
        leftParams.topMargin = verticalMargin;
        leftParams.bottomMargin = verticalMargin;
        streamMenuPanel.setLayoutParams(leftParams);

        FrameLayout.LayoutParams rightParams = (FrameLayout.LayoutParams)
                shortcutRail.getLayoutParams();
        rightParams.width = rightWidth;
        rightParams.topMargin = verticalMargin;
        rightParams.bottomMargin = verticalMargin;
        shortcutRail.setLayoutParams(rightParams);

        int leftInset = clampPixels(Math.round(leftWidth * 0.055f),
                dpToPixels(10), dpToPixels(20));
        streamMenuPanel.setPadding(leftInset, dpToPixels(12), leftInset, dpToPixels(8));
        int rightInset = clampPixels(Math.round(rightWidth * 0.05f),
                dpToPixels(6), dpToPixels(12));
        shortcutRail.setPadding(rightInset, dpToPixels(10), rightInset, dpToPixels(8));

        float density = getResources().getDisplayMetrics().density;
        float leftWidthDp = leftWidth / density;
        TextView title = findViewById(R.id.streamMenuTitle);
        if (title != null) title.setTextSize(leftWidthDp < 280 ? 18f : 22f);
        for (int i = 0; streamMenuItems != null && i < streamMenuItems.getChildCount(); i++) {
            View child = streamMenuItems.getChildAt(i);
            if (child instanceof TextView) ((TextView) child).setTextSize(leftWidthDp < 280 ? 14f : 16f);
        }

        int availableRightHeight = windowHeight - verticalMargin * 2 - dpToPixels(18);
        int childCount = Math.max(1, shortcutRailItems.getChildCount());
        int rowCount = 1 + Math.max(0, (childCount - 1 + 1) / 2);
        int rightItemHeight = clampPixels(
                availableRightHeight / Math.max(1, rowCount) - dpToPixels(6),
                dpToPixels(44), dpToPixels(50));
        float rightWidthDp = rightWidth / density;
        for (int i = 0; i < shortcutRailItems.getChildCount(); i++) {
            View child = shortcutRailItems.getChildAt(i);
            ViewGroup.LayoutParams params = child.getLayoutParams();
            params.height = rightItemHeight;
            child.setLayoutParams(params);
            if (child instanceof TextView) ((TextView) child).setTextSize(rightWidthDp < 175 ? 11f : 13f);
        }
    }

    private static int clampPixels(int value, int minimum, int maximum) {
        return Math.max(minimum, Math.min(maximum, value));
    }

    private void stopBackgroundStreamService() {
        backgroundSession = null;
        stopService(new Intent(this, com.limelight.piulink.BackgroundStreamService.class));
    }

    private void startBackgroundStreamService() {
        if (backgroundSession != null || leavingStream) return;
        backgroundSession = java.util.UUID.randomUUID().toString();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) !=
                        PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { android.Manifest.permission.POST_NOTIFICATIONS }, 48012);
        }
        SharedPreferences preferences = android.preference.PreferenceManager
                .getDefaultSharedPreferences(this);
        int minutes;
        try { minutes = Integer.parseInt(preferences.getString("list_background_timeout", "15")); }
        catch (NumberFormatException exception) { minutes = 15; }
        Intent service = new Intent(this, com.limelight.piulink.BackgroundStreamService.class)
                .putExtra(com.limelight.piulink.BackgroundStreamService.EXTRA_SESSION, backgroundSession)
                .putExtra(com.limelight.piulink.BackgroundStreamService.EXTRA_TIMEOUT_MINUTES,
                        minutes);
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) startForegroundService(service);
            else startService(service);
        } catch (IllegalStateException | SecurityException unavailable) {
            backgroundSession = null;
            Toast.makeText(this, R.string.background_stream_unavailable, Toast.LENGTH_SHORT).show();
        }
    }

    private void onLiveCaptureChanged(int width, int height) {
        if (width > 0 && height > 0) {
            float fit = Math.min(prefConfig.width / (float)width, prefConfig.height / (float)height);
            captureFractionX = width * fit / prefConfig.width;
            captureFractionY = height * fit / prefConfig.height;
        }
        viewport.captureChanged();
        applyViewport();
        if (inputHints != null) inputHints.resetViewportPoll();
    }

    private void animatePointerPan(boolean local) {
        if (streamView.getTranslationX() == viewport.x && streamView.getTranslationY() == viewport.y) return;
        panResponseMs = local ? 24f : 70f;
        if (panFrameTime == 0) {
            panFrameTime = System.nanoTime();
            streamView.postOnAnimation(panFrame);
        }
    }

    private void viewportRelative(int dx, int dy, boolean relativeToView) {
        if (!viewport.isZoomed() || softwareKeyboardVisible || pinching) return;
        int width = relativeToView ? streamView.getWidth() : prefConfig.width;
        int height = relativeToView ? streamView.getHeight() : prefConfig.height;
        if (width > 0 && height > 0 && viewport.movePointer(dx / (float)width, dy / (float)height)) {
            lastLocalViewportMotion = android.os.SystemClock.uptimeMillis();
            animatePointerPan(true);
        }
    }

    private void viewportAbsolute(int x, int y) {
        if (!viewport.isZoomed() || softwareKeyboardVisible || pinching || streamView.getWidth() == 0 || streamView.getHeight() == 0) return;
        viewport.position(x / (float)streamView.getWidth(), y / (float)streamView.getHeight(), false);
        lastLocalViewportMotion = android.os.SystemClock.uptimeMillis();
        animatePointerPan(true);
    }

    private void applyViewport() {
        if (panFrameTime != 0) {
            streamView.removeCallbacks(panFrame);
            panFrameTime = 0;
        }
        streamView.setPivotX(0f);
        streamView.setPivotY(0f);
        streamView.setScaleX(viewport.scale);
        streamView.setScaleY(viewport.scale);
        streamView.setTranslationX(viewport.x);
        streamView.setTranslationY(viewport.y);
        if (inputHints != null) inputHints.trackViewport(softwareKeyboardVisible, viewport.isZoomed());
    }

    private boolean handlePinch(View view, MotionEvent e) {
        int action = e.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) pendingPinch = pinching = false;
        if (pinching && e.getPointerCount() != 2) return true;
        if (e.getPointerCount() != 2 || e.getToolType(0) != MotionEvent.TOOL_TYPE_FINGER ||
                e.getToolType(1) != MotionEvent.TOOL_TYPE_FINGER) { pendingPinch = false; return false; }
        float ratio = view == streamView ? viewport.scale : 1f;
        float span = (float)Math.hypot(e.getX(1)-e.getX(0), e.getY(1)-e.getY(0)) * ratio;
        float mx = (e.getX(0)+e.getX(1))*0.5f*ratio, my = (e.getY(0)+e.getY(1))*0.5f*ratio;
        mx += view.getX() - streamView.getLeft();
        my += view.getY() - streamView.getTop();
        if (action == MotionEvent.ACTION_POINTER_DOWN) {
            pendingPinch = true; pinchSpan = span; pinchMidX = mx; pinchMidY = my;
        } else if (action == MotionEvent.ACTION_MOVE && (pendingPinch || pinching)) {
            if (pendingPinch) {
                float change = Math.abs(span-pinchSpan);
                double travel = Math.hypot(mx-pinchMidX, my-pinchMidY);
                int slop = android.view.ViewConfiguration.get(this).getScaledTouchSlop();
                if (Math.max(change, travel) < slop) return true;
                pendingPinch = false;
                if (change < slop || change < travel * 1.25) return false;
                pinching = true;
                if (inputHints != null) inputHints.cancelTap();
                for (TouchContext ctx : touchContextMap) {
                    ctx.cancelTouch(); ctx.setPointerCount(0);
                }
            }
            if (pinchSpan > 0 && span > 0) viewport.pinch(span / pinchSpan, mx, my);
            pinchSpan = span;
            applyViewport();
        }
        return pinching;
    }

    private int dpToPixels(float dp) {
        return Math.round(dp * getResources().getDisplayMetrics().density);
    }

    private void setupShortcutRail() {
        shortcutRail = findViewById(R.id.shortcutRail);
        shortcutRailItems = findViewById(R.id.shortcutRailItems);
        shortcutRailTrigger = findViewById(R.id.shortcutRailHandle);
        View dock = findViewById(R.id.streamQuickDock);
        shortcutRailHandle = dock != null ? dock : shortcutRailTrigger;
        if (dock != null) {
            dock.findViewById(R.id.quickStreamKeyboard).setOnClickListener(v -> {
                if (connected && !leavingStream) toggleKeyboard();
            });
            View display = dock.findViewById(R.id.quickStreamDisplay);
            display.setOnClickListener(v -> showMonitorSelectionFromStream());
            display.setOnLongClickListener(v -> { showMonitorSelectionFromStream(); return true; });
            android.view.GestureDetector gestures = new android.view.GestureDetector(this,
                    new android.view.GestureDetector.SimpleOnGestureListener() {
                @Override public boolean onDown(MotionEvent e) { return true; }
                @Override public boolean onSingleTapUp(MotionEvent e) { return display.performClick(); }
                @Override public void onLongPress(MotionEvent e) { display.performLongClick(); }
                @Override public boolean onScroll(MotionEvent first, MotionEvent now, float dx, float dy) {
                    return true;
                }
            });
            display.setOnTouchListener(new View.OnTouchListener() {
                float downX, downY;
                public boolean onTouch(View v, MotionEvent e) {
                    if (e.getActionMasked() == MotionEvent.ACTION_DOWN) {
                        downX = e.getRawX(); downY = e.getRawY(); v.setPressed(true);
                    }
                    gestures.onTouchEvent(e);
                    if (e.getActionMasked() == MotionEvent.ACTION_UP) {
                        float dx = e.getRawX()-downX, dy = e.getRawY()-downY;
                        if (Math.max(Math.abs(dx), Math.abs(dy)) >= dpToPixels(24) && v.hasWindowFocus()) {
                            if (Math.abs(dx) >= Math.abs(dy)) openCapturePicker(true, dx < 0 ? -1 : 1, 0);
                            else openCapturePicker(true, 0, dy < 0 ? -1 : 1);
                        }
                    }
                    if (e.getActionMasked() == MotionEvent.ACTION_UP || e.getActionMasked() == MotionEvent.ACTION_CANCEL)
                        v.setPressed(false);
                    return true;
                }
            });
        }

        java.util.Set<String> migratedActions = ShortcutRailConfiguration
                .migrateLegacyDefaults(prefConfig.shortcutRailActions);
        if (!migratedActions.equals(prefConfig.shortcutRailActions)) {
            prefConfig.shortcutRailActions = migratedActions;
            android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                    .putStringSet("multiselect_shortcut_rail_actions", migratedActions).apply();
        }
        List<String> actions = ShortcutRailConfiguration.orderedSelection(migratedActions);
        if (!prefConfig.shortcutRailEnabled || actions.isEmpty()) {
            shortcutRail.setVisibility(View.GONE);
            shortcutRailHandle.setVisibility(View.GONE);
            return;
        }

        shortcutRailItems.removeAllViews();
        // A pinned overlay keeps focus away from the streamed desktop. The right rail is
        // now intentionally transient, so remove the obsolete persisted pin state.
        android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                .remove("shortcut_rail_pinned").apply();
        for (String action : actions) {
            TextView button = addShortcutRailButton(
                    getShortcutRailGlyph(action),
                    getShortcutRailDescription(action),
                    ShortcutRailConfiguration.KEYBOARD.equals(action)
                            ? view -> {
                                view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                                toggleKeyboard();
                                setShortcutRailOpen(false, true);
                            }
                            : ShortcutRailConfiguration.VOICE_INPUT.equals(action)
                                    ? view -> {
                                        view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                                        startVoiceInput();
                                        setShortcutRailOpen(false, true);
                                    }
                            : null,
                    false);
            if (!ShortcutRailConfiguration.KEYBOARD.equals(action) &&
                    !ShortcutRailConfiguration.VOICE_INPUT.equals(action)) {
                button.setOnClickListener(view -> {
                    view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                    beginShortcutRailChord(action);
                    view.postDelayed(() -> {
                        releaseShortcutRailChord(action);
                        setShortcutRailOpen(false, true);
                        streamView.requestFocus();
                    }, 80);
                });
                button.setOnKeyListener((view, keyCode, event) -> {
                    if (isRemoteDpadEvent(event) && keyCode == KeyEvent.KEYCODE_DPAD_LEFT &&
                            event.getAction() == KeyEvent.ACTION_DOWN) {
                        releaseActiveShortcutRailChord();
                        setShortcutRailOpen(false, true);
                        streamView.requestFocus();
                        return true;
                    }
                    if (keyCode != KeyEvent.KEYCODE_DPAD_CENTER &&
                            keyCode != KeyEvent.KEYCODE_ENTER &&
                            keyCode != KeyEvent.KEYCODE_NUMPAD_ENTER) {
                        return false;
                    }
                    if (event.getAction() == KeyEvent.ACTION_DOWN) {
                        if (event.getRepeatCount() == 0) {
                            view.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
                            beginShortcutRailChord(action);
                            view.setPressed(true);
                        }
                        return true;
                    }
                    if (event.getAction() == KeyEvent.ACTION_UP) {
                        releaseShortcutRailChord(action);
                        view.setPressed(false);
                        setShortcutRailOpen(false, true);
                        streamView.requestFocus();
                        return true;
                    }
                    return false;
                });
            }
        }

        // The rail replaces vendor-reserved three-finger gestures. Keep it open when a
        // stream starts so Task view and Show desktop are immediately discoverable.
        shortcutRailOpen = false;
        shortcutRail.setVisibility(View.GONE);
        shortcutRailHandle.setVisibility(View.VISIBLE);
        shortcutRailTrigger.setOnClickListener(view -> {
            setShortcutRailOpen(!shortcutRailOpen, true);
            if (shortcutRailOpen) {
                focusFirstShortcutRailButton();
            }
            else {
                streamView.requestFocus();
            }
        });
    }

    // Keep the Game activity preference file so existing per-host choices survive upgrades.
    static boolean autoMicEnabled(Context context, String hostId) {
        return hostId != null && context.getSharedPreferences(Game.class.getName(), MODE_PRIVATE)
                .getBoolean("auto_microphone:" + hostId, false);
    }

    static void setAutoMicrophone(Context context, String hostId, boolean enabled) {
        if (hostId != null) context.getSharedPreferences(Game.class.getName(), MODE_PRIVATE).edit()
                .putBoolean("auto_microphone:" + hostId, enabled).apply();
    }

    private void clickMicrophone() {
        if (remoteMic != null || micPermissionPending) demandMuted = true;
        else if (hostUsingMicrophone) {
            demandCapture = true;
        }
        toggleRemoteMicrophone();
    }

    private void toggleRemoteMicrophone() {
        if (remoteMic != null || micPermissionPending) {
            stopRemoteMicrophone();
            return;
        }
        if (!connected || !activityResumed || leavingStream || serverCert == null ||
                getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION) == null) {
            showMicMessage(R.string.mic_session_required);
            return;
        }
        boolean hasMicPermission = checkSelfPermission(android.Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
        if (hasMicPermission && !hasWindowFocus()) {
            showMicMessage(R.string.mic_session_required);
            return;
        }
        micMessage = null;
        micPermissionPending = true;
        if (!hasMicPermission) {
            updateMicControls();
            updatePipAutoEnter();
            requestPermissions(new String[] { android.Manifest.permission.RECORD_AUDIO }, REQUEST_REMOTE_MIC);
            return;
        }
        micPermissionGranted = true;
        startGrantedMicrophone();
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] permissions, int[] grants) {
        super.onRequestPermissionsResult(code, permissions, grants);
        if (code != REQUEST_REMOTE_MIC || !micPermissionPending) return;
        if (grants.length > 0 && grants[0] == PackageManager.PERMISSION_GRANTED) {
            micPermissionGranted = true;
            startGrantedMicrophone();
        } else {
            demandMuted = true;
            stopRemoteMicrophone();
            updatePipAutoEnter();
            showMicMessage(R.string.mic_permission);
            if (grants.length > 0 && !shouldShowRequestPermissionRationale(android.Manifest.permission.RECORD_AUDIO)) {
                new android.app.AlertDialog.Builder(this).setMessage(R.string.mic_permission)
                        .setNegativeButton(android.R.string.cancel, null)
                        .setPositiveButton(R.string.mic_settings, (dialog, which) -> startActivity(new Intent(
                                android.provider.Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                                android.net.Uri.fromParts("package", getPackageName(), null)))).show();
            }
        }
    }

    private void startGrantedMicrophone() {
        if (!micPermissionPending || !micPermissionGranted || !activityResumed || !hasWindowFocus()) return;
        micPermissionPending = micPermissionGranted = false;
        updatePipAutoEnter();
        if (!connected || leavingStream || isFinishing() || isInPictureInPictureMode()) {
            updateMicControls();
            return;
        }
        remoteMic = new RemoteMicrophone(this, getIntent().getStringExtra(EXTRA_HOST),
                getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION), serverCert, new RemoteMicrophone.Listener() {
            public void onReady(RemoteMicrophone source, String device) {
                runOnUiThread(() -> {
                    if (source != remoteMic) return;
                    micDevice = device;
                    micMessage = getString(R.string.mic_destination, device);
                    if (!streamMenuOpen) Toast.makeText(Game.this, micMessage, Toast.LENGTH_LONG).show();
                    updateMicControls();
                });
            }
            public void onStopped(RemoteMicrophone source, int message) {
                runOnUiThread(() -> {
                    if (source != remoteMic) return;
                    if (demandCapture) demandMuted = true;
                    stopRemoteMicrophone();
                    showMicMessage(message);
                });
            }
        });
        updateMicControls();
        remoteMic.start();
    }

    private void stopRemoteMicrophone() {
        demandCapture = false;
        micPermissionPending = micPermissionGranted = false;
        RemoteMicrophone current = remoteMic;
        remoteMic = null;
        if (current != null) current.stop();
        micDevice = micMessage = null;
        updateMicControls();
    }

    private void showMicMessage(int message) {
        micMessage = getString(message);
        updateMicControls();
        if (!streamMenuOpen) Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }

    private void updateMicControls() {
        if (micButton == null) return;
        int label = micPermissionPending ? R.string.mic_wait_permission : remoteMic == null ? R.string.mic_enable
                : micDevice == null ? R.string.mic_connecting : R.string.mic_mute;
        micButton.setText(label);
        micButton.setContentDescription(getString(label));
        micButton.setSelected(micDevice != null);
        micDetail.setText(micMessage);
        micDetail.setVisibility(micMessage == null ? View.GONE : View.VISIBLE);
        boolean touch = UiHelper.isTouchscreenClient(this);
        int badgeLabel = remoteMic == null && !micPermissionPending ? R.string.mic_requested
                : micDevice != null ? R.string.lunar_live_mic : label;
        micBadge.setText(touch ? badgeLabel : label);
        micBadge.setContentDescription(getString(touch && hostUsingMicrophone && remoteMic == null ? badgeLabel : label));
        micBadge.setActivated(micDevice != null);
        micBadge.setVisibility((micPermissionPending || remoteMic != null || (hostUsingMicrophone && !demandMuted)) && !streamMenuOpen && !shortcutRailOpen && !isHidingOverlays
                ? View.VISIBLE : View.GONE);
    }

    private void setupStreamMenu() {
        streamMenuPanel = findViewById(R.id.streamMenuPanel);
        streamMenuItems = findViewById(R.id.streamMenuItems);
        streamMenuHandle = findViewById(R.id.streamMenuHandle);
        micBadge = findViewById(R.id.remoteMicBadge);
        micDetail = findViewById(R.id.remoteMicDetail);
        if (UiHelper.isTouchscreenClient(this)) {
            FrameLayout.LayoutParams badge = (FrameLayout.LayoutParams) micBadge.getLayoutParams();
            // Keep the microphone off the virtual gamepad's lower-left thumbstick.
            badge.gravity = prefConfig.onscreenController
                    ? android.view.Gravity.TOP | android.view.Gravity.CENTER_HORIZONTAL
                    : android.view.Gravity.BOTTOM | android.view.Gravity.START;
            badge.setMarginStart(prefConfig.onscreenController ? 0 : dpToPixels(12));
            badge.topMargin = dpToPixels(12);
            badge.bottomMargin = dpToPixels(12);
            micBadge.setLayoutParams(badge);
            micBadge.setOnClickListener(view -> clickMicrophone());
        } else micBadge.setOnClickListener(view -> stopRemoteMicrophone());
        streamMenuItems.removeAllViews();
        if (television) addStreamMenuButton(R.drawable.ic_play, R.string.tv_continue_stream, view -> {
            setStreamMenuOpen(false, true);
            streamView.requestFocus();
        });
        micButton = addStreamMenuButton(R.drawable.ic_stream_mic, R.string.mic_enable, view -> clickMicrophone());
        View dock = findViewById(R.id.streamQuickDock);
        if (dock == null || dock.getVisibility() != View.VISIBLE) {
            addStreamMenuButton(R.drawable.ic_computer, R.string.monitor_select_title,
                    view -> showMonitorSelectionFromStream());
            addStreamMenuButton(R.drawable.ic_stream_keyboard, R.string.shortcut_action_keyboard,
                    view -> toggleKeyboard());
        }
        localCursorMenuButton = addStreamMenuButton(R.drawable.ic_stream_cursor, R.string.title_checkbox_local_cursor, view ->
                setLocalCursorEnabled(!prefConfig.localCursor));
        performanceMenuButton = addStreamMenuButton(R.drawable.ic_stream_stats, R.string.title_enable_perf_overlay, view ->
                setPerformanceOverlayEnabled(!prefConfig.enablePerfOverlay));
        pipMenuButton = addStreamMenuButton(R.drawable.ic_stream_pip, R.string.menu_auto_pip, view ->
                setPipEnabled(!prefConfig.enablePip));
        backgroundDurationMenuButton = addStreamMenuButton(R.drawable.ic_stream_clock, R.string.background_stream_duration,
                view -> showBackgroundDurationDialog());
        addStreamMenuButton(R.drawable.ic_stream_minimize, R.string.menu_enter_pip, view -> {
            setStreamMenuOpen(false, false);
            enterBackgroundFromMenu();
        });
        addStreamMenuButton(R.drawable.ic_stream_disconnect, R.string.stream_disconnect, view -> {
            setStreamMenuOpen(false, false);
            if (television) confirmTvLeave();
            else disconnectFromStreamMenu();
        });
        updateStreamMenuSettingStates();
        streamMenuOpen = false;
        streamMenuPanel.setVisibility(View.GONE);
        streamMenuHandle.setVisibility(View.VISIBLE);
        streamMenuHandle.setOnClickListener(view -> setStreamMenuOpen(!streamMenuOpen, true));
    }

    private void showStreamMenu() {
        setStreamMenuOpen(true, true);
    }

    private TextView addStreamMenuButton(int iconResource, int textResource,
                                         View.OnClickListener clickListener) {
        TextView button = new TextView(this);
        button.setText(textResource);
        android.graphics.drawable.Drawable icon = getDrawable(iconResource).mutate();
        icon.setTint(getColor(R.color.home_device_action));
        button.setCompoundDrawablesRelativeWithIntrinsicBounds(icon, null, null, null);
        button.setCompoundDrawablePadding(dpToPixels(12));
        button.setContentDescription(getString(textResource));
        button.setTextColor(0xFFFFFFFF);
        if (UiHelper.isTouchscreenClient(this) && textResource == R.string.stream_disconnect) {
            int color = getColor(R.color.piulink_danger);
            icon.setTint(color);
            button.setTextColor(color);
        }
        button.setTextSize(16f);
        button.setMaxLines(2);
        button.setEllipsize(android.text.TextUtils.TruncateAt.END);
        button.setGravity(television ? android.view.Gravity.CENTER :
                android.view.Gravity.START | android.view.Gravity.CENTER_VERTICAL);
        button.setBackgroundResource(television ? R.drawable.bg_tv_rail_button : R.drawable.bg_stream_action);
        button.setClickable(true);
        button.setFocusable(true);
        // In touch mode, a focusable button consumes the first tap to take focus and
        // invokes its click listener only on the second tap. Keep D-pad focus support,
        // but let touchscreen taps activate menu actions immediately.
        button.setFocusableInTouchMode(false);
        button.setDefaultFocusHighlightEnabled(false);
        button.setPadding(dpToPixels(12), 0, dpToPixels(12), 0);
        button.setOnClickListener(clickListener);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        button.setMinHeight(dpToPixels(48));
        params.bottomMargin = dpToPixels(8);
        streamMenuItems.addView(button, params);
        return button;
    }

    private void focusFirstStreamMenuButton() {
        if (streamMenuItems == null || streamMenuItems.getChildCount() == 0) return;
        View first = streamMenuItems.getChildAt(0);
        first.post(() -> { if (streamMenuOpen && first.isShown()) first.requestFocus(); });
    }

    private void showMonitorSelectionFromStream() {
        openCapturePicker(false, 0, 0);
    }

    private void openCapturePicker(boolean quick, int dx, int dy) {
        if (!connected || leavingStream || isFinishing() || isDestroyed()) return;
        setStreamMenuOpen(false, false);
        setShortcutRailOpen(false, false);
        String host = getIntent().getStringExtra(EXTRA_PC_UUID);
        String address = getIntent().getStringExtra(EXTRA_HOST);
        com.limelight.piulink.MonitorSelection.StreamSwitch stop = (endApplication, afterStopped) -> {
                    if (!connected || isFinishing() || isDestroyed()) return;
                    displayedFailureDialog = true;
                    setInputGrabState(false);
                    stopConnection(afterStopped, endApplication);
                };
        com.limelight.piulink.MonitorSelection.Selected ready = (index, w, h, fps, id) -> replacePreparedStream(0, w, h, fps, id);
        com.limelight.piulink.MonitorSelection.openStreamDisplays(this, host, address,
                getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION), quick, dx, dy,
                this::onLiveCaptureChanged, stop, ready);
    }

    private void showTvReconnect(boolean automatic, int retryMessage) {
        if (leavingStream || streamPageStopped || isFinishing() || isDestroyed()) return;
        AlertDialog dialog = new AlertDialog.Builder(this)
                .setTitle(R.string.tv_connection_lost)
                .setMessage(getString(R.string.tv_reconnect_host, getIntent().getStringExtra(EXTRA_PC_NAME)))
                .setNegativeButton(R.string.tv_return_to_library, (d, which) -> cancelTvReconnect())
                .setPositiveButton(R.string.screen_retry_connection, null)
                .setOnCancelListener(d -> cancelTvReconnect()).create();
        tvReconnectDialog = dialog;
        dialog.show();
        View retry = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
        retry.setVisibility(View.GONE);
        dialog.getButton(AlertDialog.BUTTON_NEGATIVE).requestFocus();
        retry.setOnClickListener(v -> {
            retry.setVisibility(View.GONE);
            dialog.getButton(AlertDialog.BUTTON_NEGATIVE).requestFocus();
            waitForTvNetwork(dialog, SystemClock.elapsedRealtime());
        });
        stopConnection(() -> {
            if (tvReconnectDialog != dialog || isFinishing() || isDestroyed()) return;
            if (automatic) {
                long started = SystemClock.elapsedRealtime();
                reconnectHandler.postDelayed(() -> waitForTvNetwork(dialog, started), P2P_RECONNECT_DELAY_MS);
            } else {
                dialog.setMessage(getString(retryMessage));
                retry.setVisibility(View.VISIBLE);
                retry.requestFocus();
            }
        });
    }

    private void waitForTvNetwork(AlertDialog dialog, long started) {
        if (tvReconnectDialog != dialog || leavingStream || streamPageStopped || isFinishing() || isDestroyed()) return;
        android.net.ConnectivityManager network = getSystemService(android.net.ConnectivityManager.class);
        boolean online = network != null && network.getActiveNetwork() != null;
        switch (P2pReconnectPolicy.tvNetworkStep(online, SystemClock.elapsedRealtime() - started)) {
            case CONNECT:
                dismissTvReconnect();
                prepareReplacementStream(1);
                break;
            case WAIT:
                dialog.setMessage(getString(R.string.tv_network_wait));
                reconnectHandler.postDelayed(() -> waitForTvNetwork(dialog, started), 500);
                break;
            case OFFER_RETRY:
                dialog.setMessage(getString(R.string.tv_network_timeout));
                View retry = dialog.getButton(AlertDialog.BUTTON_POSITIVE);
                retry.setVisibility(View.VISIBLE);
                retry.requestFocus();
                break;
        }
    }

    private void dismissTvReconnect() {
        if (tvReconnectDialog != null) {
            tvReconnectDialog.dismiss();
            tvReconnectDialog = null;
        }
    }

    private void cancelTvReconnect() {
        leavingStream = true;
        reconnectHandler.removeCallbacksAndMessages(null);
        dismissTvReconnect();
        finish();
    }

    private void prepareReplacementStream(int attempt) {
        if (streamPageStopped || isFinishing() || isDestroyed()) return;
        com.limelight.piulink.MonitorSelection.connectDefault(this, getIntent().getStringExtra(EXTRA_PC_UUID),
                getIntent().getStringExtra(EXTRA_HOST), (index, w, h, fps, id) -> replacePreparedStream(attempt, w, h, fps, id));
    }

    private void replacePreparedStream(int attempt, int width, int height, int fps, String requestId) {
        Intent replacement = new Intent(getIntent()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        replacement.putExtra(EXTRA_STREAM_WIDTH, width);
        replacement.putExtra(EXTRA_STREAM_HEIGHT, height);
        replacement.putExtra(EXTRA_STREAM_FPS, fps);
        replacement.putExtra(EXTRA_DISPLAY_PREPARATION, requestId);
        replacement.putExtra(EXTRA_P2P_RECONNECT_ATTEMPT, attempt);
        replacementStreamIntent = replacement;
        finish();
    }

    private void setLocalCursorEnabled(boolean enabled) {
        prefConfig.localCursor = enabled;
        android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                .putBoolean("checkbox_local_cursor", enabled).apply();
        updateLocalCursor(enabled);
        updateStreamMenuSettingStates();
    }

    private void setPerformanceOverlayEnabled(boolean enabled) {
        prefConfig.enablePerfOverlay = enabled;
        android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                .putBoolean("checkbox_enable_perf_overlay", enabled).apply();
        performanceOverlayView.setVisibility(enabled ? View.VISIBLE : View.GONE);
        updateStreamMenuSettingStates();
    }

    private void setPipEnabled(boolean enabled) {
        prefConfig.enablePip = enabled;
        android.preference.PreferenceManager.getDefaultSharedPreferences(this).edit()
                .putBoolean("checkbox_enable_pip", enabled).apply();
        if (!enabled) {
            stopBackgroundStreamService();
        }
        updatePipAutoEnter();
        updateStreamMenuSettingStates();
    }

    private void updateStreamMenuSettingStates() {
        updateBooleanMenuButton(localCursorMenuButton,
                R.string.title_checkbox_local_cursor, prefConfig.localCursor);
        updateBooleanMenuButton(performanceMenuButton,
                R.string.title_enable_perf_overlay, prefConfig.enablePerfOverlay);
        updateBooleanMenuButton(pipMenuButton,
                R.string.menu_auto_pip, prefConfig.enablePip);
        if (backgroundDurationMenuButton != null) {
            SharedPreferences preferences = android.preference.PreferenceManager
                    .getDefaultSharedPreferences(this);
            String minutes = preferences.getString("list_background_timeout", "15");
            String state = "0".equals(minutes)
                    ? getString(R.string.background_duration_never)
                    : getString(R.string.background_duration_minutes_compact, minutes);
            backgroundDurationMenuButton.setText(getString(R.string.background_stream_duration) + " · " + state);
            backgroundDurationMenuButton.setContentDescription(getString(R.string.background_stream_duration) + " · " + state);
        }
    }

    private void updateBooleanMenuButton(TextView button, int titleResource, boolean enabled) {
        if (button == null) return;
        button.setText(getString(titleResource) + " · " +
                getString(enabled ? R.string.setting_state_on : R.string.setting_state_off));
        button.setContentDescription(getString(titleResource) + " · " +
                getString(enabled ? R.string.setting_state_on : R.string.setting_state_off));
        button.setSelected(enabled);
    }

    private void setStreamMenuOpen(boolean open, boolean animate) {
        if (isHidingOverlays || streamMenuPanel == null || streamMenuHandle == null) return;
        streamMenuPanel.animate().cancel();
        streamMenuOpen = open;
        if (open) { releaseForwardedKeys(); streamView.invalidateInputSession(); }
        syncKeyboardOwner();
        int travel = streamMenuPanel.getWidth() > 0 ? streamMenuPanel.getWidth() : dpToPixels(300);
        if (open) {
            if (shortcutRailOpen) setShortcutRailOpen(false, false);
            streamMenuHandle.setVisibility(View.GONE);
            streamMenuPanel.setVisibility(View.VISIBLE);
            streamMenuPanel.setTranslationX(animate ? -travel : 0);
            streamMenuPanel.animate().translationX(0).setDuration(animate ? 180 : 0)
                    .setInterpolator(new DecelerateInterpolator()).start();
            if (animate && UiHelper.isTouchscreenClient(this) &&
                    android.animation.ValueAnimator.areAnimatorsEnabled()) {
                for (int i = 0; i < streamMenuItems.getChildCount(); i++) {
                    View row = streamMenuItems.getChildAt(i);
                    row.animate().cancel();
                    row.setAlpha(.5f);
                    row.setTranslationX(-dpToPixels(9));
                    row.animate().alpha(1f).translationX(0).setStartDelay(Math.min(i, 5) * 12L)
                            .setDuration(145).setInterpolator(new DecelerateInterpolator()).start();
                }
            }
            ((ScrollView)findViewById(R.id.streamMenuScroll)).scrollTo(0, 0);
            if (!streamMenuPanel.isInTouchMode()) focusFirstStreamMenuButton();
        }
        else if (animate && streamMenuPanel.getVisibility() == View.VISIBLE) {
            streamMenuPanel.animate().translationX(-travel).setDuration(160)
                    .setInterpolator(new DecelerateInterpolator()).withEndAction(() -> {
                        streamMenuPanel.setVisibility(View.GONE);
                        streamMenuPanel.setTranslationX(0);
                        if (!isHidingOverlays) streamMenuHandle.setVisibility(View.VISIBLE);
                    }).start();
        }
        else {
            streamMenuPanel.setVisibility(View.GONE);
            streamMenuPanel.setTranslationX(0);
            if (!isHidingOverlays) streamMenuHandle.setVisibility(View.VISIBLE);
        }
        updateStreamOverlayScrim();
    }

    private void enterBackgroundFromMenu() {
        if (!connected || !getPackageManager().hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE)) {
            Toast.makeText(this, R.string.background_stream_unavailable, Toast.LENGTH_SHORT).show();
            return;
        }
        try {
            if (enterPictureInPictureMode(getPictureInPictureParams(false))) {
                backgroundTransitionRequested = true;
                startBackgroundStreamService();
            } else {
                Toast.makeText(this, R.string.background_stream_unavailable, Toast.LENGTH_SHORT).show();
            }
        }
        catch (IllegalStateException | IllegalArgumentException exception) {
            Toast.makeText(this, R.string.background_stream_unavailable, Toast.LENGTH_SHORT).show();
        }
    }

    private void disconnectFromStreamMenu() {
        if (leavingStream) return;
        leavingStream = true;
        stopBackgroundStreamService();
        displayedFailureDialog = true;
        reconnectHandler.removeCallbacksAndMessages(null);
        setInputGrabState(false);
        LimeLog.info("Leaving stream from menu; finishing Activity before connection cleanup");
        stopConnection(null, !television);
        finish();
    }

    private void confirmTvLeave() {
        AlertDialog dialog = new AlertDialog.Builder(this).setTitle(R.string.tv_leave_title)
                .setMessage(R.string.tv_leave_message)
                .setNegativeButton(R.string.tv_continue_stream, null)
                .setPositiveButton(R.string.stream_disconnect, (d, which) -> disconnectFromStreamMenu())
                .create();
        dialog.show();
        dialog.getButton(AlertDialog.BUTTON_NEGATIVE).requestFocus();
    }

    public static void disconnectBackgroundStream(String session) {
        // BroadcastReceiver callbacks run on the main thread, as do PiP and resume callbacks.
        Game game = activeInstance;
        if (session != null && game != null && session.equals(game.backgroundSession)) {
            game.disconnectFromStreamMenu();
        }
    }

    private void showBackgroundDurationDialog() {
        final int[] values = { 5, 15, 30, 60, 0 };
        String[] labels = getResources().getStringArray(R.array.background_stream_duration_names);
        SharedPreferences preferences = android.preference.PreferenceManager
                .getDefaultSharedPreferences(this);
        int current;
        try { current = Integer.parseInt(preferences.getString("list_background_timeout", "15")); }
        catch (NumberFormatException exception) { current = 15; }
        int selected = 1;
        for (int i = 0; i < values.length; i++) if (values[i] == current) selected = i;
        new AlertDialog.Builder(this)
                .setTitle(R.string.background_stream_duration)
                .setSingleChoiceItems(labels, selected, (dialog, which) -> {
                    preferences.edit().putString("list_background_timeout",
                            Integer.toString(values[which])).apply();
                    updateStreamMenuSettingStates();
                    dialog.dismiss();
                }).show();
    }

    private TextView addShortcutRailButton(String label,
                                           int contentDescriptionResource,
                                           View.OnClickListener clickListener,
                                           boolean compact) {
        TextView button = new TextView(this);
        button.setText(label);
        button.setTextColor(0xFFFFFFFF);
        button.setTextSize(14f);
        button.setSingleLine(true);
        button.setEllipsize(android.text.TextUtils.TruncateAt.END);
        button.setGravity(android.view.Gravity.CENTER);
        button.setContentDescription(getString(contentDescriptionResource));
        button.setBackgroundResource(R.drawable.bg_tv_rail_button);
        button.setClickable(true);
        button.setFocusable(true);
        // Avoid the first tap being used only to reclaim focus after the user touches
        // the streamed desktop. D-pad navigation still works outside touch mode.
        button.setFocusableInTouchMode(false);
        button.setDefaultFocusHighlightEnabled(false);
        button.setOnClickListener(clickListener);
        button.setPadding(dpToPixels(8), dpToPixels(2), dpToPixels(8), dpToPixels(2));

        GridLayout.LayoutParams params = new GridLayout.LayoutParams();
        params.width = 0;
        params.height = dpToPixels(48);
        params.columnSpec = GridLayout.spec(GridLayout.UNDEFINED, compact ? 2 : 1, 1f);
        int gap = dpToPixels(3);
        params.setMargins(gap, gap, gap, gap);
        shortcutRailItems.addView(button, params);
        return button;
    }

    private void focusFirstShortcutRailButton() {
        if (shortcutRailItems == null || shortcutRailItems.getChildCount() == 0) {
            return;
        }
        View firstButton = shortcutRailItems.getChildAt(0);
        firstButton.post(() -> {
            firstButton.requestFocus();
            shortcutRail.smoothScrollTo(0, firstButton.getTop());
        });
    }

    private void moveOverlayFocus(int direction) {
        ViewGroup items = streamMenuOpen ? streamMenuItems :
                (shortcutRailOpen ? shortcutRailItems : null);
        if (items == null || items.getChildCount() == 0) return;

        View focused = getCurrentFocus();
        int currentIndex = -1;
        for (int i = 0; i < items.getChildCount(); i++) {
            if (items.getChildAt(i) == focused) {
                currentIndex = i;
                break;
            }
        }
        int nextIndex = currentIndex < 0 ? 0 :
                Math.max(0, Math.min(items.getChildCount() - 1, currentIndex + direction));
        View next = items.getChildAt(nextIndex);
        next.requestFocus();
        if (items == streamMenuItems) next.requestRectangleOnScreen(new Rect(0, 0, next.getWidth(), next.getHeight()));
        if (items == shortcutRailItems && shortcutRail != null) {
            shortcutRail.smoothScrollTo(0, next.getTop());
        }
        LimeLog.info("Moved overlay focus to index=" + nextIndex);
    }

    private boolean isRemoteDpadEvent(KeyEvent event) {
        int source = event.getSource();
        if (television && TvStreamKeys.controller(inputSources(event))) return false;
        return (source & InputDevice.SOURCE_DPAD) == InputDevice.SOURCE_DPAD &&
                (source & InputDevice.SOURCE_GAMEPAD) != InputDevice.SOURCE_GAMEPAD &&
                (source & InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK;
    }

    private boolean handleStreamOverlayNavigation(int keyCode, KeyEvent event) {
        if (keyCode != KeyEvent.KEYCODE_DPAD_LEFT && keyCode != KeyEvent.KEYCODE_DPAD_RIGHT) {
            return false;
        }

        if (!isRemoteDpadEvent(event)) {
            return false;
        }

        // The stream owns focus and forwards gamepad D-pad events to the host. Only a TV
        // remote may use these directions for PiuLink's visible edge affordances.
        if (event.getAction() == KeyEvent.ACTION_UP || event.getRepeatCount() > 0) {
            return true;
        }
        if (event.getAction() != KeyEvent.ACTION_DOWN) {
            return false;
        }

        if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT && streamMenuHandle != null &&
                !isHidingOverlays) {
            LimeLog.info("Opening stream menu from DPAD left: device=" +
                    event.getDeviceId() + " source=" + event.getSource());
            showStreamMenu();
            return true;
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT && shortcutRail != null &&
                shortcutRailItems != null && shortcutRailItems.getChildCount() > 0 &&
                !isHidingOverlays) {
            LimeLog.info("Opening shortcut rail from DPAD right: device=" +
                    event.getDeviceId() + " source=" + event.getSource());
            if (!shortcutRailOpen) {
                setShortcutRailOpen(true, true);
            }
            focusFirstShortcutRailButton();
            return true;
        }
        return false;
    }

    private boolean handleStreamOverlayMotionNavigation(MotionEvent event) {
        if ((event.getSource() & InputDevice.SOURCE_CLASS_JOYSTICK) == 0 ||
                event.getActionMasked() != MotionEvent.ACTION_MOVE) {
            return false;
        }

        float hatX = event.getAxisValue(MotionEvent.AXIS_HAT_X);
        float hatY = event.getAxisValue(MotionEvent.AXIS_HAT_Y);
        int direction = hatX <= -0.5f ? -1 : hatX >= 0.5f ? 1 : 0;
        int verticalDirection = hatY <= -0.5f ? -1 : hatY >= 0.5f ? 1 : 0;
        if (streamMenuOpen || shortcutRailOpen) {
            int previousVerticalDirection = overlayHatYDirection;
            if (verticalDirection != overlayHatYDirection) {
                overlayHatYDirection = verticalDirection;
                if (verticalDirection != 0) {
                    moveOverlayFocus(verticalDirection);
                    return true;
                }
            }
            if (hatY != 0 || previousVerticalDirection != 0) return true;
        }
        if (streamMenuOpen) {
            int previousDirection = overlayHatDirection;
            overlayHatDirection = direction;
            if (direction > 0) {
                setStreamMenuOpen(false, true);
                streamView.requestFocus();
                return true;
            }
            return hatX != 0 || previousDirection != 0;
        }
        if (shortcutRailOpen || isHidingOverlays) {
            if (overlayHatDirection != 0 && direction == 0) {
                overlayHatDirection = 0;
                return true;
            }
            return false;
        }

        // With no overlay open, every controller HAT direction belongs to the streamed game.
        // Reset overlay-only tracking and let ControllerHandler forward this event unchanged.
        overlayHatDirection = 0;
        overlayHatYDirection = 0;
        return false;
    }

    private String getShortcutRailGlyph(String action) {
        switch (action) {
            case ShortcutRailConfiguration.WINDOW_NEXT:
                return getString(R.string.shortcut_label_window_next);
            case ShortcutRailConfiguration.WINDOW_PREVIOUS:
                return getString(R.string.shortcut_label_window_previous);
            case ShortcutRailConfiguration.TASK_VIEW:
                return getString(R.string.shortcut_label_task_view);
            case ShortcutRailConfiguration.SHOW_DESKTOP:
                return getString(R.string.shortcut_label_show_desktop);
            case ShortcutRailConfiguration.CLOSE_WINDOW:
                return getString(R.string.shortcut_label_close_window);
            case ShortcutRailConfiguration.TASK_MANAGER:
                return getString(R.string.shortcut_label_task_manager);
            case ShortcutRailConfiguration.COPY:
                return getString(R.string.shortcut_label_copy);
            case ShortcutRailConfiguration.PASTE:
                return getString(R.string.shortcut_label_paste);
            case ShortcutRailConfiguration.UNDO:
                return getString(R.string.shortcut_label_undo);
            case ShortcutRailConfiguration.ESCAPE:
                return "Esc";
            case ShortcutRailConfiguration.KEYBOARD:
                return getString(R.string.shortcut_label_keyboard);
            case ShortcutRailConfiguration.VOICE_INPUT:
                return "🎙";
            default:
                return "?";
        }
    }

    private int getShortcutRailDescription(String action) {
        switch (action) {
            case ShortcutRailConfiguration.WINDOW_NEXT:
                return R.string.shortcut_action_window_next;
            case ShortcutRailConfiguration.WINDOW_PREVIOUS:
                return R.string.shortcut_action_window_previous;
            case ShortcutRailConfiguration.TASK_VIEW:
                return R.string.shortcut_action_task_view;
            case ShortcutRailConfiguration.SHOW_DESKTOP:
                return R.string.shortcut_action_show_desktop;
            case ShortcutRailConfiguration.CLOSE_WINDOW:
                return R.string.shortcut_action_close_window;
            case ShortcutRailConfiguration.TASK_MANAGER:
                return R.string.shortcut_action_task_manager;
            case ShortcutRailConfiguration.COPY:
                return R.string.shortcut_action_copy;
            case ShortcutRailConfiguration.PASTE:
                return R.string.shortcut_action_paste;
            case ShortcutRailConfiguration.UNDO:
                return R.string.shortcut_action_undo;
            case ShortcutRailConfiguration.ESCAPE:
                return R.string.shortcut_action_escape;
            case ShortcutRailConfiguration.KEYBOARD:
                return R.string.shortcut_action_keyboard;
            case ShortcutRailConfiguration.VOICE_INPUT:
                return R.string.shortcut_action_voice_input;
            default:
                return R.string.title_shortcut_rail_actions;
        }
    }

    private int getShortcutRailKeyCode(String action) {
        ShortcutChord chord = ShortcutChord.forAction(action);
        return chord == null ? KeyEvent.KEYCODE_UNKNOWN : chord.getKeyCode();
    }

    private byte getShortcutRailModifiers(String action) {
        ShortcutChord chord = ShortcutChord.forAction(action);
        return chord == null ? 0 : chord.getModifiers();
    }

    private void beginShortcutRailChord(String action) {
        releaseActiveShortcutRailChord();
        int keyCode = getShortcutRailKeyCode(action);
        if (keyCode == KeyEvent.KEYCODE_UNKNOWN || conn == null || keyboardTranslator == null) {
            LimeLog.warning("Unable to map shortcut rail action: " + action);
            return;
        }

        byte modifiers = getShortcutRailModifiers(action);
        sendShortcutModifiers(modifiers, true);
        if (!sendTranslatedShortcutKey(keyCode, KeyboardPacket.KEY_DOWN, modifiers)) {
            sendShortcutModifiers(modifiers, false);
            return;
        }

        activeShortcutRailAction = action;
        activeShortcutRailKeyCode = keyCode;
        activeShortcutRailModifiers = modifiers;
    }

    private void releaseShortcutRailChord(String action) {
        if (action.equals(activeShortcutRailAction)) {
            releaseActiveShortcutRailChord();
        }
    }

    private void releaseActiveShortcutRailChord() {
        if (activeShortcutRailKeyCode == KeyEvent.KEYCODE_UNKNOWN) {
            return;
        }

        if (conn != null && keyboardTranslator != null) {
            sendTranslatedShortcutKey(activeShortcutRailKeyCode, KeyboardPacket.KEY_UP,
                    activeShortcutRailModifiers);
            sendShortcutModifiers(activeShortcutRailModifiers, false);
        }
        activeShortcutRailAction = null;
        activeShortcutRailKeyCode = KeyEvent.KEYCODE_UNKNOWN;
        activeShortcutRailModifiers = 0;
    }

    private void sendShortcutModifiers(byte modifiers, boolean down) {
        byte state = down ? 0 : modifiers;
        int[] keyCodes = {
                KeyEvent.KEYCODE_CTRL_LEFT,
                KeyEvent.KEYCODE_SHIFT_LEFT,
                KeyEvent.KEYCODE_ALT_LEFT,
                KeyEvent.KEYCODE_META_LEFT
        };
        byte[] masks = {
                KeyboardPacket.MODIFIER_CTRL,
                KeyboardPacket.MODIFIER_SHIFT,
                KeyboardPacket.MODIFIER_ALT,
                KeyboardPacket.MODIFIER_META
        };

        if (down) {
            for (int index = 0; index < keyCodes.length; index++) {
                if ((modifiers & masks[index]) != 0) {
                    state |= masks[index];
                    sendTranslatedShortcutKey(keyCodes[index], KeyboardPacket.KEY_DOWN, state);
                }
            }
        }
        else {
            for (int index = keyCodes.length - 1; index >= 0; index--) {
                if ((modifiers & masks[index]) != 0) {
                    state &= ~masks[index];
                    sendTranslatedShortcutKey(keyCodes[index], KeyboardPacket.KEY_UP, state);
                }
            }
        }
    }

    private boolean sendTranslatedShortcutKey(int androidKeyCode, byte direction, byte modifiers) {
        short translated = keyboardTranslator.translate(androidKeyCode, -1);
        if (translated == 0) {
            LimeLog.warning("Unable to map shortcut key: " + androidKeyCode);
            return false;
        }
        sendTrackedKeyboardInput(translated, direction, modifiers, (byte)0);
        return true;
    }

    private void updateLocalCursor(boolean enabled) {
        int generation = ++cursorUpdateGeneration;
        cursorHandler.removeCallbacksAndMessages(null);
        cursorVisible = false;
        inputCaptureProvider.hideCursor();
        if (!connected || conn == null || !conn.supportsPiuLinkControl()) return;
        requestCursorMode(conn, enabled, generation, 0);
    }

    private void requestCursorMode(NvConnection session, boolean enabled, int generation, int poll) {
        if (generation != cursorUpdateGeneration || !connected || session != conn) return;
        if (poll % 5 == 0 && !session.setHostCursorHidden(enabled)) return;
        int ack = session.getHostCursorMode();
        if (ack == (enabled ? 1 : 2)) {
            cursorVisible = enabled;
            if (enabled) {
                inputCaptureProvider.showCursor();
                cursorHandler.postDelayed(() -> monitorHostCursor(session, generation), 500);
            }
            return;
        }
        if (ack == 0 || poll >= 14) {
            // An unacknowledged hide may have taken effect. Restore the remote cursor explicitly.
            if (enabled) session.setHostCursorHidden(false);
            return;
        }
        cursorHandler.postDelayed(() -> requestCursorMode(session, enabled, generation, poll + 1), 100);
    }

    private void monitorHostCursor(NvConnection session, int generation) {
        if (generation != cursorUpdateGeneration || !connected || session != conn) return;
        if (session.getHostCursorMode() != 1) {
            // The Host restores its captured cursor if another streaming client joins.
            cursorVisible = false;
            inputCaptureProvider.hideCursor();
            return;
        }
        cursorHandler.postDelayed(() -> monitorHostCursor(session, generation), 500);
    }

    private void restoreHostCursorCapture() {
        ++cursorUpdateGeneration;
        cursorHandler.removeCallbacksAndMessages(null);
        cursorVisible = false;
        if (conn != null) conn.setHostCursorHidden(false);
    }

    private void setShortcutRailOpen(boolean open, boolean animate) {
        if (!prefConfig.shortcutRailEnabled || isHidingOverlays ||
                shortcutRail == null || shortcutRailHandle == null) {
            return;
        }

        shortcutRail.animate().cancel();
        shortcutRailHandle.animate().cancel();
        if (open && streamMenuOpen) setStreamMenuOpen(false, false);
        shortcutRailOpen = open;
        if (open) { releaseForwardedKeys(); streamView.invalidateInputSession(); }
        syncKeyboardOwner();
        int travel = shortcutRail.getWidth() > 0 ? shortcutRail.getWidth() : dpToPixels(190);
        if (open) {
            shortcutRailHandle.setVisibility(View.GONE);
            shortcutRail.setVisibility(View.VISIBLE);
            shortcutRail.setTranslationX(animate ? travel : 0);
            shortcutRail.animate()
                    .translationX(0)
                    .setDuration(animate ? 180 : 0)
                    .setInterpolator(new DecelerateInterpolator())
                    .start();
        }
        else if (animate && shortcutRail.getVisibility() == View.VISIBLE) {
            shortcutRail.animate()
                    .translationX(travel)
                    .setDuration(160)
                    .setInterpolator(new DecelerateInterpolator())
                    .withEndAction(() -> {
                        shortcutRail.setVisibility(View.GONE);
                        shortcutRail.setTranslationX(0);
                        if (!isHidingOverlays) {
                            shortcutRailHandle.setVisibility(View.VISIBLE);
                        }
                    })
                    .start();
        }
        else {
            shortcutRail.setVisibility(View.GONE);
            shortcutRail.setTranslationX(0);
            shortcutRailHandle.setVisibility(View.VISIBLE);
        }
        updateStreamOverlayScrim();
    }

    private void updateStreamOverlayScrim() {
        updateMicControls();
        if (streamOverlayScrim != null) {
            streamOverlayScrim.setVisibility(streamMenuOpen || shortcutRailOpen
                    ? View.VISIBLE : View.GONE);
        }
    }

    private void restoreShortcutRailVisibility() {
        if (shortcutRail == null || shortcutRailHandle == null ||
                !prefConfig.shortcutRailEnabled ||
                ShortcutRailConfiguration.orderedSelection(prefConfig.shortcutRailActions).isEmpty()) {
            return;
        }

        shortcutRail.setVisibility(shortcutRailOpen ? View.VISIBLE : View.GONE);
        shortcutRail.setTranslationX(0);
        shortcutRailHandle.setVisibility(shortcutRailOpen ? View.GONE : View.VISIBLE);
    }

    private byte getLiTouchTypeFromEvent(MotionEvent event) {
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_POINTER_DOWN:
                return MoonBridge.LI_TOUCH_EVENT_DOWN;

            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_POINTER_UP:
                if ((event.getFlags() & MotionEvent.FLAG_CANCELED) != 0) {
                    return MoonBridge.LI_TOUCH_EVENT_CANCEL;
                }
                else {
                    return MoonBridge.LI_TOUCH_EVENT_UP;
                }

            case MotionEvent.ACTION_MOVE:
                return MoonBridge.LI_TOUCH_EVENT_MOVE;

            case MotionEvent.ACTION_CANCEL:
                // ACTION_CANCEL applies to *all* pointers in the gesture, so it maps to CANCEL_ALL
                // rather than CANCEL. For a single pointer cancellation, that's indicated via
                // FLAG_CANCELED on a ACTION_POINTER_UP.
                // https://developer.android.com/develop/ui/views/touch-and-input/gestures/multi
                return MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL;

            case MotionEvent.ACTION_HOVER_ENTER:
            case MotionEvent.ACTION_HOVER_MOVE:
                return MoonBridge.LI_TOUCH_EVENT_HOVER;

            case MotionEvent.ACTION_HOVER_EXIT:
                return MoonBridge.LI_TOUCH_EVENT_HOVER_LEAVE;

            case MotionEvent.ACTION_BUTTON_PRESS:
            case MotionEvent.ACTION_BUTTON_RELEASE:
                return MoonBridge.LI_TOUCH_EVENT_BUTTON_ONLY;

            default:
               return -1;
        }
    }

    private float[] getStreamViewRelativeNormalizedXY(View view, MotionEvent event, int pointerIndex) {
        float normalizedX = event.getX(pointerIndex);
        float normalizedY = event.getY(pointerIndex);

        // For the containing background view, we must subtract the origin
        // of the StreamView to get video-relative coordinates.
        if (view != streamView) {
            normalizedX = (normalizedX + view.getX() - streamView.getX()) / streamView.getScaleX();
            normalizedY = (normalizedY + view.getY() - streamView.getY()) / streamView.getScaleY();
        }

        normalizedX = Math.max(normalizedX, 0.0f);
        normalizedY = Math.max(normalizedY, 0.0f);

        normalizedX = Math.min(normalizedX, streamView.getWidth());
        normalizedY = Math.min(normalizedY, streamView.getHeight());

        normalizedX /= streamView.getWidth();
        normalizedY /= streamView.getHeight();

        return new float[] { normalizedX, normalizedY };
    }

    private static float normalizeValueInRange(float value, InputDevice.MotionRange range) {
        return (value - range.getMin()) / range.getRange();
    }

    private static float getPressureOrDistance(MotionEvent event, int pointerIndex) {
        InputDevice dev = event.getDevice();
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_HOVER_ENTER:
            case MotionEvent.ACTION_HOVER_MOVE:
            case MotionEvent.ACTION_HOVER_EXIT:
                // Hover events report distance
                if (dev != null) {
                    InputDevice.MotionRange distanceRange = dev.getMotionRange(MotionEvent.AXIS_DISTANCE, event.getSource());
                    if (distanceRange != null) {
                        return normalizeValueInRange(event.getAxisValue(MotionEvent.AXIS_DISTANCE, pointerIndex), distanceRange);
                    }
                }
                return 0.0f;

            default:
                // Other events report pressure
                return event.getPressure(pointerIndex);
        }
    }

    private static short getRotationDegrees(MotionEvent event, int pointerIndex) {
        InputDevice dev = event.getDevice();
        if (dev != null) {
            if (dev.getMotionRange(MotionEvent.AXIS_ORIENTATION, event.getSource()) != null) {
                short rotationDegrees = (short) Math.toDegrees(event.getOrientation(pointerIndex));
                if (rotationDegrees < 0) {
                    rotationDegrees += 360;
                }
                return rotationDegrees;
            }
        }
        return MoonBridge.LI_ROT_UNKNOWN;
    }

    private static float[] polarToCartesian(float r, float theta) {
        return new float[] { (float)(r * Math.cos(theta)), (float)(r * Math.sin(theta)) };
    }

    private static float cartesianToR(float[] point) {
        return (float)Math.sqrt(Math.pow(point[0], 2) + Math.pow(point[1], 2));
    }

    private float[] getStreamViewNormalizedContactArea(MotionEvent event, int pointerIndex) {
        float orientation;

        // If the orientation is unknown, we'll just assume it's at a 45 degree angle and scale it by
        // X and Y scaling factors evenly.
        if (event.getDevice() == null || event.getDevice().getMotionRange(MotionEvent.AXIS_ORIENTATION, event.getSource()) == null) {
            orientation = (float)(Math.PI / 4);
        }
        else {
            orientation = event.getOrientation(pointerIndex);
        }

        float contactAreaMajor, contactAreaMinor;
        switch (event.getActionMasked()) {
            // Hover events report the tool size
            case MotionEvent.ACTION_HOVER_ENTER:
            case MotionEvent.ACTION_HOVER_MOVE:
            case MotionEvent.ACTION_HOVER_EXIT:
                contactAreaMajor = event.getToolMajor(pointerIndex);
                contactAreaMinor = event.getToolMinor(pointerIndex);
                break;

            // Other events report contact area
            default:
                contactAreaMajor = event.getTouchMajor(pointerIndex);
                contactAreaMinor = event.getTouchMinor(pointerIndex);
                break;
        }

        // The contact area major axis is parallel to the orientation, so we simply convert
        // polar to cartesian coordinates using the orientation as theta.
        float[] contactAreaMajorCartesian = polarToCartesian(contactAreaMajor, orientation);

        // The contact area minor axis is perpendicular to the contact area major axis (and thus
        // the orientation), so rotate the orientation angle by 90 degrees.
        float[] contactAreaMinorCartesian = polarToCartesian(contactAreaMinor, (float)(orientation + (Math.PI / 2)));

        // Normalize the contact area to the stream view size
        contactAreaMajorCartesian[0] = Math.min(Math.abs(contactAreaMajorCartesian[0]), streamView.getWidth()) / streamView.getWidth();
        contactAreaMinorCartesian[0] = Math.min(Math.abs(contactAreaMinorCartesian[0]), streamView.getWidth()) / streamView.getWidth();
        contactAreaMajorCartesian[1] = Math.min(Math.abs(contactAreaMajorCartesian[1]), streamView.getHeight()) / streamView.getHeight();
        contactAreaMinorCartesian[1] = Math.min(Math.abs(contactAreaMinorCartesian[1]), streamView.getHeight()) / streamView.getHeight();

        // Convert the normalized values back into polar coordinates
        return new float[] { cartesianToR(contactAreaMajorCartesian), cartesianToR(contactAreaMinorCartesian) };
    }

    private boolean sendPenEventForPointer(View view, MotionEvent event, byte eventType, byte toolType, int pointerIndex) {
        byte penButtons = 0;
        if ((event.getButtonState() & MotionEvent.BUTTON_STYLUS_PRIMARY) != 0) {
            penButtons |= MoonBridge.LI_PEN_BUTTON_PRIMARY;
        }
        if ((event.getButtonState() & MotionEvent.BUTTON_STYLUS_SECONDARY) != 0) {
            penButtons |= MoonBridge.LI_PEN_BUTTON_SECONDARY;
        }

        byte tiltDegrees = MoonBridge.LI_TILT_UNKNOWN;
        InputDevice dev = event.getDevice();
        if (dev != null) {
            if (dev.getMotionRange(MotionEvent.AXIS_TILT, event.getSource()) != null) {
                tiltDegrees = (byte)Math.toDegrees(event.getAxisValue(MotionEvent.AXIS_TILT, pointerIndex));
            }
        }

        float[] normalizedCoords = getStreamViewRelativeNormalizedXY(view, event, pointerIndex);
        float[] normalizedContactArea = getStreamViewNormalizedContactArea(event, pointerIndex);
        return conn.sendPenEvent(eventType, toolType, penButtons,
                normalizedCoords[0], normalizedCoords[1],
                getPressureOrDistance(event, pointerIndex),
                normalizedContactArea[0], normalizedContactArea[1],
                getRotationDegrees(event, pointerIndex), tiltDegrees) != MoonBridge.LI_ERR_UNSUPPORTED;
    }

    private static byte convertToolTypeToStylusToolType(MotionEvent event, int pointerIndex) {
        switch (event.getToolType(pointerIndex)) {
            case MotionEvent.TOOL_TYPE_ERASER:
                return MoonBridge.LI_TOOL_TYPE_ERASER;
            case MotionEvent.TOOL_TYPE_STYLUS:
                return MoonBridge.LI_TOOL_TYPE_PEN;
            default:
                return MoonBridge.LI_TOOL_TYPE_UNKNOWN;
        }
    }

    private boolean trySendPenEvent(View view, MotionEvent event) {
        byte eventType = getLiTouchTypeFromEvent(event);
        if (eventType < 0) {
            return false;
        }

        if (event.getActionMasked() == MotionEvent.ACTION_MOVE) {
            // Move events may impact all active pointers
            boolean handledStylusEvent = false;
            for (int i = 0; i < event.getPointerCount(); i++) {
                byte toolType = convertToolTypeToStylusToolType(event, i);
                if (toolType == MoonBridge.LI_TOOL_TYPE_UNKNOWN) {
                    // Not a stylus pointer, so skip it
                    continue;
                }
                else {
                    // This pointer is a stylus, so we'll report that we handled this event
                    handledStylusEvent = true;
                }

                if (!sendPenEventForPointer(view, event, eventType, toolType, i)) {
                    // Pen events aren't supported by the host
                    return false;
                }
            }
            return handledStylusEvent;
        }
        else if (event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
            // Cancel impacts all active pointers
            return conn.sendPenEvent(MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL, MoonBridge.LI_TOOL_TYPE_UNKNOWN, (byte)0,
                    0, 0, 0, 0, 0,
                    MoonBridge.LI_ROT_UNKNOWN, MoonBridge.LI_TILT_UNKNOWN) != MoonBridge.LI_ERR_UNSUPPORTED;
        }
        else {
            // Up, Down, and Hover events are specific to the action index
            byte toolType = convertToolTypeToStylusToolType(event, event.getActionIndex());
            if (toolType == MoonBridge.LI_TOOL_TYPE_UNKNOWN) {
                // Not a stylus event
                return false;
            }
            return sendPenEventForPointer(view, event, eventType, toolType, event.getActionIndex());
        }
    }

    private boolean sendTouchEventForPointer(View view, MotionEvent event, byte eventType, int pointerIndex) {
        float[] normalizedCoords = getStreamViewRelativeNormalizedXY(view, event, pointerIndex);
        float[] normalizedContactArea = getStreamViewNormalizedContactArea(event, pointerIndex);
        return conn.sendTouchEvent(eventType, event.getPointerId(pointerIndex),
                normalizedCoords[0], normalizedCoords[1],
                getPressureOrDistance(event, pointerIndex),
                normalizedContactArea[0], normalizedContactArea[1],
                getRotationDegrees(event, pointerIndex)) != MoonBridge.LI_ERR_UNSUPPORTED;
    }

    private boolean trySendTouchEvent(View view, MotionEvent event) {
        byte eventType = getLiTouchTypeFromEvent(event);
        if (eventType < 0) {
            return false;
        }

        if (event.getActionMasked() == MotionEvent.ACTION_MOVE) {
            // Move events may impact all active pointers
            for (int i = 0; i < event.getPointerCount(); i++) {
                if (!sendTouchEventForPointer(view, event, eventType, i)) {
                    return false;
                }
            }
            return true;
        }
        else if (event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
            // Cancel impacts all active pointers
            return conn.sendTouchEvent(MoonBridge.LI_TOUCH_EVENT_CANCEL_ALL, 0,
                    0, 0, 0, 0, 0,
                    MoonBridge.LI_ROT_UNKNOWN) != MoonBridge.LI_ERR_UNSUPPORTED;
        }
        else {
            // Up, Down, and Hover events are specific to the action index
            return sendTouchEventForPointer(view, event, eventType, event.getActionIndex());
        }
    }

    // Returns true if the event was consumed
    // NB: View is only present if called from a view callback
    private boolean handleMotionEvent(View view, MotionEvent event) {
        // Pass through mouse/touch/joystick input if we're not grabbing
        if (!grabbedInput) {
            return false;
        }

        int eventSource = event.getSource();
        int deviceSources = event.getDevice() != null ? event.getDevice().getSources() : 0;
        if ((eventSource & InputDevice.SOURCE_CLASS_JOYSTICK) != 0) {
            if (controllerHandler.handleMotionEvent(event)) {
                return true;
            }
        }
        else if ((deviceSources & InputDevice.SOURCE_CLASS_JOYSTICK) != 0 && controllerHandler.tryHandleTouchpadEvent(event)) {
            return true;
        }
        else if ((eventSource & InputDevice.SOURCE_CLASS_POINTER) != 0 ||
                 (eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0 ||
                 eventSource == InputDevice.SOURCE_MOUSE_RELATIVE)
        {
            // This case is for mice and non-finger touch devices
            if (eventSource == InputDevice.SOURCE_MOUSE ||
                    (eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0 || // SOURCE_TOUCHPAD
                    eventSource == InputDevice.SOURCE_MOUSE_RELATIVE ||
                    (event.getPointerCount() >= 1 &&
                            (event.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE ||
                                    event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS ||
                                    event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER)) ||
                    eventSource == 12290) // 12290 = Samsung DeX mode desktop mouse
            {
                int buttonState = event.getButtonState();
                int changedButtons = buttonState ^ lastButtonState;

                // The DeX touchpad on the Fold 4 sends proper right click events using BUTTON_SECONDARY,
                // but doesn't send BUTTON_PRIMARY for a regular click. Instead it sends ACTION_DOWN/UP,
                // so we need to fix that up to look like a sane input event to process it correctly.
                if (eventSource == 12290) {
                    if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
                        buttonState |= MotionEvent.BUTTON_PRIMARY;
                    }
                    else if (event.getAction() == MotionEvent.ACTION_UP) {
                        buttonState &= ~MotionEvent.BUTTON_PRIMARY;
                    }
                    else {
                        // We may be faking the primary button down from a previous event,
                        // so be sure to add that bit back into the button state.
                        buttonState |= (lastButtonState & MotionEvent.BUTTON_PRIMARY);
                    }

                    changedButtons = buttonState ^ lastButtonState;
                }

                // Ignore mouse input if we're not capturing from our input source
                if (!inputCaptureProvider.isCapturingActive()) {
                    relativeMouseMotion.reset();
                    // We return true here because otherwise the events may end up causing
                    // Android to synthesize d-pad events.
                    return true;
                }

                // Always update the position before sending any button events. If we're
                // dealing with a stylus without hover support, our position might be
                // significantly different than before.
                if (inputCaptureProvider.eventHasRelativeMouseAxes(event)) {
                    if (event.getActionMasked() == MotionEvent.ACTION_MOVE ||
                            event.getActionMasked() == MotionEvent.ACTION_HOVER_MOVE) {
                        mouseInputStats.record(event.getHistorySize() > 0 ? event.getHistoricalEventTime(0) :
                                        event.getEventTime(), event.getEventTime(),
                                android.os.SystemClock.uptimeMillis(), event.getHistorySize());
                    }
                    relativeMouseMotion.update(event.getDeviceId(),
                            inputCaptureProvider.getRelativeAxisX(event),
                            inputCaptureProvider.getRelativeAxisY(event));
                    short deltaX = relativeMouseMotion.getX();
                    short deltaY = relativeMouseMotion.getY();

                    if (deltaX != 0 || deltaY != 0) {
                        viewportRelative(deltaX, deltaY, prefConfig.absoluteMouseMode || prefConfig.localCursor);
                        if (prefConfig.absoluteMouseMode || prefConfig.localCursor) {
                            // NB: view may be null, but we can unconditionally use streamView because we don't need to adjust
                            // relative axis deltas for the position of the streamView within the parent's coordinate system.
                            conn.sendMouseMoveAsMousePosition(deltaX, deltaY, (short)streamView.getWidth(), (short)streamView.getHeight());
                        }
                        else {
                            conn.sendMouseMove(deltaX, deltaY);
                        }
                    }
                }
                else if ((eventSource & InputDevice.SOURCE_CLASS_POSITION) != 0) {
                    // If this input device is not associated with the view itself (like a trackpad),
                    // we'll convert the device-specific coordinates to use to send the cursor position.
                    // This really isn't ideal but it's probably better than nothing.
                    //
                    // Trackpad on newer versions of Android (Oreo and later) should be caught by the
                    // relative axes case above. If we get here, we're on an older version that doesn't
                    // support pointer capture.
                    InputDevice device = event.getDevice();
                    if (device != null) {
                        InputDevice.MotionRange xRange = device.getMotionRange(MotionEvent.AXIS_X, eventSource);
                        InputDevice.MotionRange yRange = device.getMotionRange(MotionEvent.AXIS_Y, eventSource);

                        // All touchpads coordinate planes should start at (0, 0)
                        if (xRange != null && yRange != null && xRange.getMin() == 0 && yRange.getMin() == 0) {
                            int xMax = (int)xRange.getMax();
                            int yMax = (int)yRange.getMax();

                            // Touchpads must be smaller than (65535, 65535)
                            if (xMax <= Short.MAX_VALUE && yMax <= Short.MAX_VALUE) {
                                conn.sendMousePosition((short)event.getX(), (short)event.getY(),
                                                       (short)xMax, (short)yMax);
                            }
                        }
                    }
                }
                else if (view != null && trySendPenEvent(view, event)) {
                    // If our host supports pen events, send it directly
                    return true;
                }
                else if (view != null) {
                    // Otherwise send absolute position based on the view for SOURCE_CLASS_POINTER
                    updateMousePosition(view, event);
                }

                if (event.getActionMasked() == MotionEvent.ACTION_SCROLL) {
                    // Send the vertical scroll packet
                    conn.sendMouseHighResScroll((short)(event.getAxisValue(MotionEvent.AXIS_VSCROLL) * 120));
                    conn.sendMouseHighResHScroll((short)(event.getAxisValue(MotionEvent.AXIS_HSCROLL) * 120));
                }

                if ((changedButtons & MotionEvent.BUTTON_PRIMARY) != 0) {
                    if ((buttonState & MotionEvent.BUTTON_PRIMARY) != 0) {
                        if (inputHints != null) inputHints.cancelTap();
                        mousePressedAt = event.getEventTime();
                        mousePressedX = event.getRawX(); mousePressedY = event.getRawY();
                        conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                    }
                    else {
                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                        if (event.getEventTime() - mousePressedAt < 500 &&
                                Math.hypot(event.getRawX() - mousePressedX, event.getRawY() - mousePressedY) < dpToPixels(12)) onRemoteTap();
                    }
                }

                // Mouse secondary or stylus primary is right click (stylus down is left click)
                if ((changedButtons & (MotionEvent.BUTTON_SECONDARY | MotionEvent.BUTTON_STYLUS_PRIMARY)) != 0) {
                    if ((buttonState & (MotionEvent.BUTTON_SECONDARY | MotionEvent.BUTTON_STYLUS_PRIMARY)) != 0) {
                        conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
                    }
                    else {
                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
                    }
                }

                // Mouse tertiary or stylus secondary is middle click
                if ((changedButtons & (MotionEvent.BUTTON_TERTIARY | MotionEvent.BUTTON_STYLUS_SECONDARY)) != 0) {
                    if ((buttonState & (MotionEvent.BUTTON_TERTIARY | MotionEvent.BUTTON_STYLUS_SECONDARY)) != 0) {
                        conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_MIDDLE);
                    }
                    else {
                        conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_MIDDLE);
                    }
                }

                if (prefConfig.mouseNavButtons) {
                    if ((changedButtons & MotionEvent.BUTTON_BACK) != 0) {
                        if ((buttonState & MotionEvent.BUTTON_BACK) != 0) {
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_X1);
                        }
                        else {
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_X1);
                        }
                    }

                    if ((changedButtons & MotionEvent.BUTTON_FORWARD) != 0) {
                        if ((buttonState & MotionEvent.BUTTON_FORWARD) != 0) {
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_X2);
                        }
                        else {
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_X2);
                        }
                    }
                }

                // Handle stylus presses
                if (event.getPointerCount() == 1 && event.getActionIndex() == 0) {
                    if (event.getActionMasked() == MotionEvent.ACTION_DOWN) {
                        if (event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS) {
                            lastAbsTouchDownTime = event.getEventTime();
                            lastAbsTouchDownX = event.getX(0);
                            lastAbsTouchDownY = event.getY(0);

                            // Stylus is left click
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_LEFT);
                        } else if (event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER) {
                            lastAbsTouchDownTime = event.getEventTime();
                            lastAbsTouchDownX = event.getX(0);
                            lastAbsTouchDownY = event.getY(0);

                            // Eraser is right click
                            conn.sendMouseButtonDown(MouseButtonPacket.BUTTON_RIGHT);
                        }
                    }
                    else if (event.getActionMasked() == MotionEvent.ACTION_UP || event.getActionMasked() == MotionEvent.ACTION_CANCEL) {
                        if (event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS) {
                            lastAbsTouchUpTime = event.getEventTime();
                            lastAbsTouchUpX = event.getX(0);
                            lastAbsTouchUpY = event.getY(0);

                            // Stylus is left click
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                        } else if (event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER) {
                            lastAbsTouchUpTime = event.getEventTime();
                            lastAbsTouchUpX = event.getX(0);
                            lastAbsTouchUpY = event.getY(0);

                            // Eraser is right click
                            conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
                        }
                    }
                }

                lastButtonState = buttonState;
            }
            // This case is for fingers
            else
            {
                if (virtualController != null &&
                        (virtualController.getControllerMode() == VirtualController.ControllerMode.MoveButtons ||
                         virtualController.getControllerMode() == VirtualController.ControllerMode.ResizeButtons)) {
                    // Ignore presses when the virtual controller is being configured
                    return true;
                }

                // If this is the parent view, we'll offset our coordinates to appear as if they
                // are relative to the StreamView like our StreamView touch events are.
                float xOffset, yOffset;
                if (view != streamView && !prefConfig.touchscreenTrackpad) {
                    xOffset = -streamView.getX();
                    yOffset = -streamView.getY();
                }
                else {
                    xOffset = 0.f;
                    yOffset = 0.f;
                }

                int actionIndex = event.getActionIndex();

                int eventX = (int)(event.getX(actionIndex) + xOffset);
                int eventY = (int)(event.getY(actionIndex) + yOffset);

                // TODO: Re-enable native touch when have a better solution for handling
                // cancelled touches from Android system gestures.
                /*if (!prefConfig.touchscreenTrackpad && trySendTouchEvent(view, event)) {
                    // If this host supports touch events and absolute touch is enabled,
                    // send it directly as a touch event.
                    return true;
                }*/

                TouchContext context = getTouchContext(actionIndex);
                if (context == null) {
                    return false;
                }

                switch (event.getActionMasked())
                {
                case MotionEvent.ACTION_POINTER_DOWN:
                case MotionEvent.ACTION_DOWN:
                    for (TouchContext touchContext : touchContextMap) {
                        touchContext.setPointerCount(event.getPointerCount());
                    }
                    context.touchDownEvent(eventX, eventY, event.getEventTime(), true);
                    break;
                case MotionEvent.ACTION_POINTER_UP:
                case MotionEvent.ACTION_UP:
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && (event.getFlags() & MotionEvent.FLAG_CANCELED) != 0) {
                        context.cancelTouch();
                    }
                    else {
                        context.touchUpEvent(eventX, eventY, event.getEventTime());
                    }

                    for (TouchContext touchContext : touchContextMap) {
                        touchContext.setPointerCount(event.getPointerCount() - 1);
                    }
                    if (actionIndex == 0 && event.getPointerCount() > 1 && !context.isCancelled()) {
                        // The original secondary touch now becomes primary
                        context.touchDownEvent(
                                (int)(event.getX(1) + xOffset),
                                (int)(event.getY(1) + yOffset),
                                event.getEventTime(), false);
                    }
                    break;
                case MotionEvent.ACTION_MOVE:
                    // ACTION_MOVE is special because it always has actionIndex == 0
                    // We'll call the move handlers for all indexes manually
                    touchInputStats.record(event.getHistorySize() > 0 ? event.getHistoricalEventTime(0) :
                                    event.getEventTime(), event.getEventTime(),
                            android.os.SystemClock.uptimeMillis(), event.getHistorySize());

                    // First process the historical events
                    for (int i = 0; i < event.getHistorySize(); i++) {
                        for (TouchContext aTouchContextMap : touchContextMap) {
                            if (aTouchContextMap.getActionIndex() < event.getPointerCount())
                            {
                                aTouchContextMap.touchMoveEvent(
                                        (int)(event.getHistoricalX(aTouchContextMap.getActionIndex(), i) + xOffset),
                                        (int)(event.getHistoricalY(aTouchContextMap.getActionIndex(), i) + yOffset),
                                        event.getHistoricalEventTime(i));
                            }
                        }
                    }

                    // Now process the current values
                    for (TouchContext aTouchContextMap : touchContextMap) {
                        if (aTouchContextMap.getActionIndex() < event.getPointerCount())
                        {
                            aTouchContextMap.touchMoveEvent(
                                    (int)(event.getX(aTouchContextMap.getActionIndex()) + xOffset),
                                    (int)(event.getY(aTouchContextMap.getActionIndex()) + yOffset),
                                    event.getEventTime());
                        }
                    }
                    break;
                case MotionEvent.ACTION_CANCEL:
                    for (TouchContext aTouchContext : touchContextMap) {
                        aTouchContext.cancelTouch();
                        aTouchContext.setPointerCount(0);
                    }
                    break;
                default:
                    return false;
                }
            }

            // Handled a known source
            return true;
        }

        // Unknown class
        return false;
    }

    @Override
    public boolean onGenericMotionEvent(MotionEvent event) {
        if (handleStreamOverlayMotionNavigation(event)) return true;
        return handleMotionEvent(null, event) || super.onGenericMotionEvent(event);

    }

    private void updateMousePosition(View touchedView, MotionEvent event) {
        // X and Y are already relative to the provided view object
        float eventX, eventY;

        // For our StreamView itself, we can use the coordinates unmodified.
        if (touchedView == streamView) {
            eventX = event.getX(0);
            eventY = event.getY(0);
        }
        else {
            // For the containing background view, we must subtract the origin
            // of the StreamView to get video-relative coordinates.
            eventX = event.getX(0) - streamView.getX();
            eventY = event.getY(0) - streamView.getY();
        }

        if (event.getPointerCount() == 1 && event.getActionIndex() == 0 &&
                (event.getToolType(0) == MotionEvent.TOOL_TYPE_ERASER ||
                event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS))
        {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                case MotionEvent.ACTION_HOVER_ENTER:
                case MotionEvent.ACTION_HOVER_EXIT:
                case MotionEvent.ACTION_HOVER_MOVE:
                    if (event.getEventTime() - lastAbsTouchUpTime <= STYLUS_UP_DEAD_ZONE_DELAY &&
                            Math.sqrt(Math.pow(eventX - lastAbsTouchUpX, 2) + Math.pow(eventY - lastAbsTouchUpY, 2)) <= STYLUS_UP_DEAD_ZONE_RADIUS) {
                        // Enforce a small deadzone between touch up and hover or touch down to allow more precise double-clicking
                        return;
                    }
                    break;

                case MotionEvent.ACTION_MOVE:
                case MotionEvent.ACTION_UP:
                    if (event.getEventTime() - lastAbsTouchDownTime <= STYLUS_DOWN_DEAD_ZONE_DELAY &&
                            Math.sqrt(Math.pow(eventX - lastAbsTouchDownX, 2) + Math.pow(eventY - lastAbsTouchDownY, 2)) <= STYLUS_DOWN_DEAD_ZONE_RADIUS) {
                        // Enforce a small deadzone between touch down and move or touch up to allow more precise double-clicking
                        return;
                    }
                    break;
            }
        }

        // We may get values slightly outside our view region on ACTION_HOVER_ENTER and ACTION_HOVER_EXIT.
        // Normalize these to the view size. We can't just drop them because we won't always get an event
        // right at the boundary of the view, so dropping them would result in our cursor never really
        // reaching the sides of the screen.
        eventX = Math.min(Math.max(eventX, 0), streamView.getWidth());
        eventY = Math.min(Math.max(eventY, 0), streamView.getHeight());

        viewportAbsolute((int)eventX, (int)eventY);
        conn.sendMousePosition((short)eventX, (short)eventY, (short)streamView.getWidth(), (short)streamView.getHeight());
    }

    @Override
    public boolean onGenericMotion(View view, MotionEvent event) {
        // A listener attached directly to StreamView runs before Activity.onGenericMotionEvent().
        // Handle overlay HAT navigation here before ControllerHandler consumes the event.
        if (handleStreamOverlayMotionNavigation(event)) return true;
        return handleMotionEvent(view, event);
    }

    @SuppressLint("ClickableViewAccessibility")
    @Override
    public boolean onTouch(View view, MotionEvent event) {
        if (event.getAction() == MotionEvent.ACTION_DOWN) {
            // Tell the OS not to buffer input events for us
            //
            // NB: This is still needed even when we call the newer requestUnbufferedDispatch()!
            view.requestUnbufferedDispatch(event);


        }

        if (handlePinch(view, event)) {
            if (event.getActionMasked() == MotionEvent.ACTION_UP || event.getActionMasked() == MotionEvent.ACTION_CANCEL)
                pinching = pendingPinch = false;
            return true;
        }
        if (event.getToolType(0) == MotionEvent.TOOL_TYPE_FINGER &&
                (viewport.scale != 1f || viewport.x != 0f || viewport.y != 0f)) {
            // The trackpad needs stable physical deltas while the viewport pans. Direct
            // touches (including their history) must map back into the video instead.
            MotionEvent mapped = MotionEvent.obtain(event);
            try {
                if (prefConfig.touchscreenTrackpad && view == streamView) {
                    mapped.transform(streamView.getMatrix());
                    mapped.offsetLocation(streamView.getLeft(), streamView.getTop());
                } else if (!prefConfig.touchscreenTrackpad && view != streamView) {
                    mapped.offsetLocation(view.getX()-streamView.getX(), view.getY()-streamView.getY());
                    android.graphics.Matrix inverse = new android.graphics.Matrix();
                    inverse.setScale(1f/viewport.scale, 1f/viewport.scale);
                    mapped.transform(inverse);
                    view = streamView;
                }
                return handleMotionEvent(view, mapped);
            } finally { mapped.recycle(); }
        }
        return handleMotionEvent(view, event);
    }

    @Override
    public boolean dispatchTouchEvent(MotionEvent event) {
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) {
            if (inputHints != null) inputHints.cancelTap();
            consumeOverlayGesture = false;
        }
        if (consumeOverlayGesture) {
            if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_CANCEL) consumeOverlayGesture = false;
            return true;
        }
        if (action == MotionEvent.ACTION_DOWN) {
            boolean openRight = !isHidingOverlays && containsRawPoint(shortcutRailTrigger, event.getRawX(), event.getRawY());
            boolean openLeft = !isHidingOverlays && containsRawPoint(streamMenuHandle, event.getRawX(), event.getRawY());
            if (openRight) {
                setShortcutRailOpen(!shortcutRailOpen, true);
            } else if (openLeft) {
                setStreamMenuOpen(!streamMenuOpen, true);
            } else if (shortcutRailOpen && isTouchOutside(event, shortcutRail, shortcutRailHandle)) {
                releaseActiveShortcutRailChord();
                setShortcutRailOpen(false, true);
            } else if (streamMenuOpen && isTouchOutside(event, streamMenuPanel, streamMenuHandle)) {
                setStreamMenuOpen(false, true);
            } else {
                return super.dispatchTouchEvent(event);
            }
            if (!streamMenuOpen && !shortcutRailOpen) streamView.requestFocus();
            consumeOverlayGesture = true;
            return true;
        }
        return super.dispatchTouchEvent(event);
    }

    private static boolean isTouchOutside(MotionEvent event, View panel, View handle) {
        return !containsRawPoint(panel, event.getRawX(), event.getRawY()) &&
                !containsRawPoint(handle, event.getRawX(), event.getRawY());
    }

    private static boolean containsRawPoint(View view, float rawX, float rawY) {
        if (view == null || !view.isShown()) return false;
        int[] location = new int[2];
        view.getLocationOnScreen(location);
        return rawX >= location[0] && rawX < location[0] + view.getWidth() &&
                rawY >= location[1] && rawY < location[1] + view.getHeight();
    }

    @Override
    public void stageStarting(final String stage) {
        if (connectionStartedAt == 0) connectionStartedAt = SystemClock.elapsedRealtime();
        stageStartedAt = SystemClock.elapsedRealtime();
        LimeLog.info("PiuLink connection stage starting: " + stage);
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (spinner != null) {
                    spinner.setMessage(getResources().getString(R.string.conn_starting) + " " + stage);
                }
            }
        });
    }

    @Override
    public void stageComplete(String stage) {
        LimeLog.info("PiuLink connection stage completed: " + stage + " in " +
                (SystemClock.elapsedRealtime() - stageStartedAt) + " ms");
    }

    private void stopConnection() {
        stopConnection(null);
    }

    private void stopConnection(Runnable afterStopped) {
        stopConnection(afterStopped, false);
    }

    private boolean leavingStream;

    private static int inputSources(KeyEvent event) {
        InputDevice device = event.getDevice();
        return event.getSource() | (device == null ? 0 : device.getSources());
    }

    private boolean dispatchTvStreamKey(KeyEvent event) {
        int code = event.getKeyCode();
        boolean down = event.getAction() == KeyEvent.ACTION_DOWN;
        boolean up = event.getAction() == KeyEvent.ACTION_UP;
        if (tvKeys.ownsFollowup(event.getDeviceId(), code, event.getDownTime(), up)) return true;
        if (!connected || leavingStream || (!down && !up)) return false;
        int sources = inputSources(event);
        boolean east = TvStreamKeys.east(code, event.getScanCode(), sources);
        boolean overlay = streamMenuOpen || shortcutRailOpen;
        if (!overlay && east) {
            if (canSendRemoteText()) {
                KeyEvent button = code == KeyEvent.KEYCODE_BUTTON_B ? event : new KeyEvent(
                        event.getDownTime(), event.getEventTime(), event.getAction(), KeyEvent.KEYCODE_BUTTON_B,
                        event.getRepeatCount(), event.getMetaState(), event.getDeviceId(), event.getScanCode(),
                        event.getFlags(), event.getSource());
                if (down) controllerHandler.handleButtonDown(button);
                else controllerHandler.handleButtonUp(button);
            }
            return true;
        }
        if (!overlay && code == KeyEvent.KEYCODE_BACK && !TvStreamKeys.controller(sources)
                && !event.isFromSource(InputDevice.SOURCE_MOUSE)
                && !event.isFromSource(InputDevice.SOURCE_MOUSE_RELATIVE)) {
            if (up && !event.isCanceled()) disconnectFromStreamMenu();
            return true;
        }
        boolean navigation = code >= KeyEvent.KEYCODE_DPAD_UP && code <= KeyEvent.KEYCODE_DPAD_CENTER;
        boolean activation = code == KeyEvent.KEYCODE_BUTTON_A || code == KeyEvent.KEYCODE_ENTER
                || code == KeyEvent.KEYCODE_NUMPAD_ENTER;
        if (down && event.getRepeatCount() == 0 && overlay &&
                (east || code == KeyEvent.KEYCODE_BACK || activation || navigation)) {
            tvKeys.ownDown(event.getDeviceId(), code, event.getDownTime());
        }
        return false;
    }

    private boolean handleTvMenuKey(KeyEvent event) {
        if (!television || event.getKeyCode() != KeyEvent.KEYCODE_MENU ||
                TvStreamKeys.controller(inputSources(event)) || !connected || leavingStream) return false;
        if (event.getAction() == KeyEvent.ACTION_DOWN && event.getRepeatCount() == 0) {
            if (shortcutRailOpen) {
                releaseActiveShortcutRailChord();
                setShortcutRailOpen(false, false);
            }
            setStreamMenuOpen(!streamMenuOpen, true);
            if (streamMenuOpen) focusFirstStreamMenuButton();
            else streamView.requestFocus();
        }
        return true;
    }

    @Override
    public void onBackPressed() {
        if (streamMenuOpen) {
            setStreamMenuOpen(false, true);
            streamView.requestFocus();
            return;
        }
        if (shortcutRailOpen) {
            releaseActiveShortcutRailChord();
            setShortcutRailOpen(false, true);
            streamView.requestFocus();
            return;
        }
        if (television && connected && !leavingStream) {
            disconnectFromStreamMenu();
            return;
        }
        // Cleanup may already be running or stalled. It must never keep the stream Activity
        // visible after the user explicitly asks to leave.
        if (leavingStream) {
            LimeLog.info("Back/B received while cleanup is active; forcing Activity finish");
            finish();
            return;
        }
        leavingStream = true;
        if (connecting || connected) {
            displayedFailureDialog = true;
            reconnectHandler.removeCallbacksAndMessages(null);
            setInputGrabState(false);
            Toast.makeText(this, "正在断开串流…", Toast.LENGTH_SHORT).show();
            LimeLog.info("Leaving stream with Back/B; finishing Activity before connection cleanup");
            stopConnection(null, !television);
            finish();
        } else {
            finish();
        }
    }

    private void stopConnection(Runnable afterStopped, boolean endSession) {
        runOnUiThread(() -> { pendingDictation = null; stopRemoteMicrophone(); });
        if (connecting || connected) {
            final boolean quitOwnedSession = endSession;
            final String hostUuid = getIntent().getStringExtra(EXTRA_PC_UUID);
            restoreHostCursorCapture();
            releaseForwardedKeys();
            if (streamView != null) streamView.invalidateInputSession();
            connecting = connected = false;
            runOnUiThread(() -> { syncKeyboardOwner(); updateInputHints(); });
            autoBitrateHandler.removeCallbacks(contentAdaptiveControlRetry);
            updatePipAutoEnter();

            controllerHandler.stop();

            // Update GameManager state to indicate we're no longer in game
            UiHelper.notifyStreamEnded(this);

            // Stop may take a few hundred ms to do some network I/O to tell
            // the server we're going away and clean up. Let it run in a separate
            // thread to keep things smooth for the UI. Inside moonlight-common,
            // we prevent another thread from starting a connection before and
            // during the process of stopping this one.
            new Thread() {
                public void run() {
                    boolean switching = quitOwnedSession && afterStopped != null;
                    boolean handoff = switching && com.limelight.piulink.MonitorSelection.beginSwitch(
                            Game.this, getIntent().getStringExtra(EXTRA_HOST), getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION));
                    conn.stop(quitOwnedSession);
                    if (switching && !handoff) {
                        // A user-confirmed switch has already quit the session. Release its lease now
                        // so the next preparation need not wait for the abandoned-stream grace period.
                        com.limelight.piulink.MonitorSelection.release(Game.this, getIntent().getStringExtra(EXTRA_HOST),
                                getIntent().getStringExtra(EXTRA_DISPLAY_PREPARATION));
                    }
                    if (quitOwnedSession && hostUuid != null) {
                        // The library can resume while the Host is still processing our quit.
                        runOnUiThread(() -> ((PiuLinkApplication) getApplication()).refreshRunningAppForHost(hostUuid));
                    }
                    if (afterStopped != null) runOnUiThread(afterStopped);
                }
            }.start();
        } else if (afterStopped != null) {
            runOnUiThread(afterStopped);
        }
    }

    @Override
    public void stageFailed(final String stage, final int portFlags, final int errorCode) {
        // Perform a connection test if the failure could be due to a blocked port
        // This does network I/O, so don't do it on the main thread.
        final boolean privatePeerPath = NetHelper.isActiveNetworkVpn(this) ||
                NetHelper.isPrivateHostAddress(getIntent().getStringExtra(EXTRA_HOST));
        final int portTestResult = privatePeerPath
                ? MoonBridge.ML_TEST_RESULT_INCONCLUSIVE
                : MoonBridge.testClientConnectivity(ServerHelper.CONNECTION_TEST_SERVER, 443, portFlags);

        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (spinner != null) {
                    spinner.dismiss();
                    spinner = null;
                }

                if (!displayedFailureDialog) {
                    displayedFailureDialog = true;
                    LimeLog.severe(stage + " failed: " + errorCode);

                    if (television && privatePeerPath && errorCode == -1 && "RTSP handshake".equals(stage)) {
                        int priorAttempts = getIntent().getIntExtra(EXTRA_P2P_RECONNECT_ATTEMPT, 0);
                        showTvReconnect(priorAttempts == 0, R.string.tv_handshake_retry);
                        return;
                    }

                    // If video initialization failed and the surface is still valid, display extra information for the user
                    if (stage.contains("video") && streamView.getHolder().getSurface().isValid()) {
                        Toast.makeText(Game.this, getResources().getText(R.string.video_decoder_init_failed), Toast.LENGTH_LONG).show();
                    }

                    String dialogText = getResources().getString(R.string.conn_error_msg) + " " + stage +" (error "+errorCode+")";

                    if (portFlags != 0) {
                        dialogText += "\n\n" + getResources().getString(R.string.check_ports_msg) + "\n" +
                                MoonBridge.stringifyPortFlags(portFlags, "\n");
                    }

                    if (portTestResult != MoonBridge.ML_TEST_RESULT_INCONCLUSIVE && portTestResult != 0)  {
                        dialogText += "\n\n" + getResources().getString(R.string.nettest_text_blocked);
                    }

                    Dialog.displayDialog(Game.this, getResources().getString(R.string.conn_error_title), dialogText, true);
                }
            }
        });
    }

    @Override
    public void connectionTerminated(final int errorCode) {
        runOnUiThread(this::stopRemoteMicrophone);
        autoBitrateHandler.removeCallbacks(autoBitrateStableSample);
        autoBitrateHandler.removeCallbacks(contentAdaptiveControlRetry);

        // Perform a connection test if the failure could be due to a blocked port
        // This does network I/O, so don't do it on the main thread.
        final int portFlags = MoonBridge.getPortFlagsFromTerminationErrorCode(errorCode);
        final boolean privatePeerPath = NetHelper.isActiveNetworkVpn(this) ||
                NetHelper.isPrivateHostAddress(getIntent().getStringExtra(EXTRA_HOST));
        // Public port tests don't diagnose an encrypted tailnet path and can delay recovery.
        final int portTestResult = privatePeerPath
                ? MoonBridge.ML_TEST_RESULT_INCONCLUSIVE
                : MoonBridge.testClientConnectivity(ServerHelper.CONNECTION_TEST_SERVER,443, portFlags);

        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                // Let the display go to sleep now
                getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

                // Stop processing controller input
                controllerHandler.stop();

                // Ungrab input
                setInputGrabState(false);

                if (!displayedFailureDialog) {
                    int reconnectAttempt = getIntent().getIntExtra(EXTRA_P2P_RECONNECT_ATTEMPT, 0);
                    boolean exiting = isFinishing() || leavingStream || streamPageStopped;
                    boolean automatic = P2pReconnectPolicy.shouldRetry(errorCode, privatePeerPath, reconnectAttempt, exiting);
                    if (television && (automatic || P2pReconnectPolicy.offerManualTvRetry(errorCode, privatePeerPath, reconnectAttempt, exiting))) {
                        displayedFailureDialog = true;
                        LimeLog.warning("TV stream interrupted: " + errorCode + ", automatic retry=" + automatic);
                        showTvReconnect(automatic, R.string.tv_network_timeout);
                        return;
                    }
                    if (automatic) {
                        displayedFailureDialog = true;
                        LimeLog.warning("Transient P2P termination " + errorCode +
                                "; reconnecting once after path roaming");
                        Toast.makeText(Game.this, R.string.p2p_reconnecting, Toast.LENGTH_SHORT).show();
                        stopConnection(() -> reconnectHandler.postDelayed(
                                () -> prepareReplacementStream(reconnectAttempt + 1), P2P_RECONNECT_DELAY_MS));
                        return;
                    }

                    displayedFailureDialog = true;
                    LimeLog.severe("Connection terminated: " + errorCode);
                    stopConnection();

                    // Display the error dialog if it was an unexpected termination.
                    // Otherwise, just finish the activity immediately.
                    if (errorCode != MoonBridge.ML_ERROR_GRACEFUL_TERMINATION) {
                        String message;

                        if (portTestResult != MoonBridge.ML_TEST_RESULT_INCONCLUSIVE && portTestResult != 0) {
                            // If we got a blocked result, that supersedes any other error message
                            message = getResources().getString(R.string.nettest_text_blocked);
                        }
                        else {
                            switch (errorCode) {
                                case MoonBridge.ML_ERROR_NO_VIDEO_TRAFFIC:
                                    message = getResources().getString(R.string.no_video_received_error);
                                    break;

                                case MoonBridge.ML_ERROR_NO_VIDEO_FRAME:
                                    message = getResources().getString(R.string.no_frame_received_error);
                                    break;

                                case MoonBridge.ML_ERROR_UNEXPECTED_EARLY_TERMINATION:
                                case MoonBridge.ML_ERROR_PROTECTED_CONTENT:
                                    message = getResources().getString(R.string.early_termination_error);
                                    break;

                                case MoonBridge.ML_ERROR_FRAME_CONVERSION:
                                    message = getResources().getString(R.string.frame_conversion_error);
                                    break;

                                default:
                                    String errorCodeString;
                                    // We'll assume large errors are hex values
                                    if (Math.abs(errorCode) > 1000) {
                                        errorCodeString = Integer.toHexString(errorCode);
                                    }
                                    else {
                                        errorCodeString = Integer.toString(errorCode);
                                    }
                                    message = getResources().getString(R.string.conn_terminated_msg) + "\n\n" +
                                            getResources().getString(R.string.error_code_prefix) + " " + errorCodeString;
                                    break;
                            }
                        }

                        if (portFlags != 0) {
                            message += "\n\n" + getResources().getString(R.string.check_ports_msg) + "\n" +
                                    MoonBridge.stringifyPortFlags(portFlags, "\n");
                        }

                        Dialog.displayDialog(Game.this, getResources().getString(R.string.conn_terminated_title),
                                message, true);
                    }
                    else {
                        finish();
                    }
                }
            }
        });
    }

    @Override
    public void connectionStatusUpdate(final int connectionStatus) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (prefConfig.disableWarnings) {
                    return;
                }

                if (connectionStatus == MoonBridge.CONN_STATUS_POOR) {
                    if (autoBitrateSession != null && conn != null && conn.getAppliedLiveBitrate() > 0) {
                        notificationOverlayView.setText(R.string.auto_bitrate_poor_network);
                        requestedNotificationOverlayVisibility = View.VISIBLE;
                    } else if (prefConfig.bitrate > 5000) {
                        notificationOverlayView.setText(getResources().getString(R.string.slow_connection_msg));
                        requestedNotificationOverlayVisibility = View.VISIBLE;
                    }
                    else {
                        notificationOverlayView.setText(getResources().getString(R.string.poor_connection_msg));
                        requestedNotificationOverlayVisibility = View.VISIBLE;
                    }
                }
                else if (connectionStatus == MoonBridge.CONN_STATUS_OKAY) {
                    requestedNotificationOverlayVisibility = View.GONE;
                }

                if (!isHidingOverlays) {
                    notificationOverlayView.setVisibility(requestedNotificationOverlayVisibility);
                }
            }
        });
    }

    @Override
    public void connectionStarted() {
        LimeLog.info("PiuLink stream connected in " +
                (connectionStartedAt == 0 ? 0 : SystemClock.elapsedRealtime() - connectionStartedAt) + " ms");
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                if (spinner != null) {
                    spinner.dismiss();
                    spinner = null;
                }

                // A late handshake must not resurrect a stream after Home/lock/exit.
                if (streamPageStopped || isFinishing() || isDestroyed()) {
                    if (connecting && conn != null) {
                        stopConnection(null, !television);
                    }
                    return;
                }
                connected = true; syncKeyboardOwner();
                if (television) TvLastSession.connected(Game.this, getIntent());
                updateInputHints();
                updateMicControls();
                streamConnectedAtMs = SystemClock.elapsedRealtime();
                videoStallReported = false;
                connecting = false;
                contentAdaptiveAttempts = 0;
                contentAdaptiveControlTimedOut = false;
                autoBitrateHandler.removeCallbacks(contentAdaptiveControlRetry);
                autoBitrateHandler.post(contentAdaptiveControlRetry);
                updatePipAutoEnter();
                // A recovered stream that remains stable earns one future roaming retry.
                reconnectHandler.postDelayed(() -> getIntent().putExtra(
                        EXTRA_P2P_RECONNECT_ATTEMPT, 0), P2P_RECONNECT_RESET_MS);

                if (autoBitrateSession != null) {
                    autoBitrateHandler.removeCallbacks(autoBitrateStableSample);
                    autoBitrateHandler.postDelayed(
                            autoBitrateStableSample,
                            5_000);
                }

                // Hide the mouse cursor now after a short delay.
                // Doing it before dismissing the spinner seems to be undone
                // when the spinner gets displayed. On Android Q, even now
                // is too early to capture. We will delay a second to allow
                // the spinner to dismiss before capturing.
                Handler h = new Handler();
                h.postDelayed(new Runnable() {
                    @Override
                    public void run() {
                        if (!connected || streamPageStopped || isFinishing() || isDestroyed()) return;
                        setInputGrabState(true);
                        updateLocalCursor(prefConfig.localCursor);
                        // The Host has already selected the stable Sunshine device ID before
                        // this connection. Replaying a numeric F-key shortcut here can target a
                        // different display after Sunshine restarts and reorders its inventory.
                    }
                }, 500);

                // Keep the display on
                getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

                // Update GameManager state to indicate we're in game
                UiHelper.notifyStreamConnected(Game.this);

                hideSystemUi(1000);
            }
        });

        // Report this shortcut being used (off the main thread to prevent ANRs)
        ComputerDetails computer = new ComputerDetails();
        computer.name = pcName;
        computer.uuid = Game.this.getIntent().getStringExtra(EXTRA_PC_UUID);
        ShortcutHelper shortcutHelper = new ShortcutHelper(this);
        shortcutHelper.reportComputerShortcutUsed(computer);
        if (appName != null) {
            // This may be null if launched from the "Resume Session" PC context menu item
            shortcutHelper.reportGameLaunched(computer, app);
        }
    }

    @Override
    public void displayMessage(final String message) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                Toast.makeText(Game.this, message, Toast.LENGTH_LONG).show();
            }
        });
    }

    @Override
    public void displayTransientMessage(final String message) {
        if (!prefConfig.disableWarnings) {
            runOnUiThread(new Runnable() {
                @Override
                public void run() {
                    Toast.makeText(Game.this, message, Toast.LENGTH_LONG).show();
                }
            });
        }
    }

    @Override
    public void rumble(short controllerNumber, short lowFreqMotor, short highFreqMotor) {
        LimeLog.info(String.format((Locale)null, "Rumble on gamepad %d: %04x %04x", controllerNumber, lowFreqMotor, highFreqMotor));

        controllerHandler.handleRumble(controllerNumber, lowFreqMotor, highFreqMotor);
    }

    @Override
    public void rumbleTriggers(short controllerNumber, short leftTrigger, short rightTrigger) {
        LimeLog.info(String.format((Locale)null, "Rumble on gamepad triggers %d: %04x %04x", controllerNumber, leftTrigger, rightTrigger));

        controllerHandler.handleRumbleTriggers(controllerNumber, leftTrigger, rightTrigger);
    }

    @Override
    public void setHdrMode(boolean enabled, byte[] hdrMetadata) {
        LimeLog.info("Display HDR mode: " + (enabled ? "enabled" : "disabled"));
        decoderRenderer.setHdrMode(enabled, hdrMetadata);
    }

    @Override
    public void setMotionEventState(short controllerNumber, byte motionType, short reportRateHz) {
        controllerHandler.handleSetMotionEventState(controllerNumber, motionType, reportRateHz);
    }

    @Override
    public void setControllerLED(short controllerNumber, byte r, byte g, byte b) {
        controllerHandler.handleSetControllerLED(controllerNumber, r, g, b);
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        if (!surfaceCreated) {
            throw new IllegalStateException("Surface changed before creation!");
        }

        if (!attemptedConnection) {
            attemptedConnection = true;

            // Update GameManager state to indicate we're "loading" while connecting
            UiHelper.notifyStreamConnecting(Game.this);

            decoderRenderer.setRenderTarget(holder);
            connecting = true;
            conn.start(new AndroidAudioRenderer(Game.this, prefConfig.enableAudioFx),
                    decoderRenderer, Game.this);
        }
    }

    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        float desiredFrameRate;

        surfaceCreated = true;

        // Android will pick the lowest matching refresh rate for a given frame rate value, so we want
        // to report the true FPS value if refresh rate reduction is enabled. We also report the true
        // FPS value if there's no suitable matching refresh rate. In that case, Android could try to
        // select a lower refresh rate that avoids uneven pull-down (ex: 30 Hz for a 60 FPS stream on
        // a display that maxes out at 50 Hz).
        if (mayReduceRefreshRate() || desiredRefreshRate < prefConfig.fps) {
            desiredFrameRate = prefConfig.fps;
        }
        else {
            // Otherwise, we will pretend that our frame rate matches the refresh rate we picked in
            // prepareDisplayForRendering(). This will usually be the highest refresh rate that our
            // frame rate evenly divides into, which ensures the lowest possible display latency.
            desiredFrameRate = desiredRefreshRate;
        }

        // Tell the OS about our frame rate to allow it to adapt the display refresh rate appropriately
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            // We want to change frame rate even if it's not seamless, since prepareDisplayForRendering()
            // will not set the display mode on S+ if it only differs by the refresh rate. It depends
            // on us to trigger the frame rate switch here.
            holder.getSurface().setFrameRate(desiredFrameRate,
                    Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE,
                    Surface.CHANGE_FRAME_RATE_ALWAYS);
        }
        else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            holder.getSurface().setFrameRate(desiredFrameRate,
                    Surface.FRAME_RATE_COMPATIBILITY_FIXED_SOURCE);
        }
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        if (!surfaceCreated) {
            throw new IllegalStateException("Surface destroyed before creation!");
        }

        if (attemptedConnection) {
            // Let the decoder know immediately that the surface is gone
            decoderRenderer.prepareForStop();

            if (connecting || connected) {
                stopConnection(null, !television && (isFinishing() || leavingStream || isInPictureInPictureMode()));
            }
        }
    }

    @Override
    public void mouseMove(int deltaX, int deltaY) {
        conn.sendMouseMove((short) deltaX, (short) deltaY);
    }

    @Override
    public void mouseButtonEvent(int buttonId, boolean down) {
        byte buttonIndex;

        switch (buttonId)
        {
        case EvdevListener.BUTTON_LEFT:
            buttonIndex = MouseButtonPacket.BUTTON_LEFT;
            break;
        case EvdevListener.BUTTON_MIDDLE:
            buttonIndex = MouseButtonPacket.BUTTON_MIDDLE;
            break;
        case EvdevListener.BUTTON_RIGHT:
            buttonIndex = MouseButtonPacket.BUTTON_RIGHT;
            break;
        case EvdevListener.BUTTON_X1:
            buttonIndex = MouseButtonPacket.BUTTON_X1;
            break;
        case EvdevListener.BUTTON_X2:
            buttonIndex = MouseButtonPacket.BUTTON_X2;
            break;
        default:
            LimeLog.warning("Unhandled button: "+buttonId);
            return;
        }

        if (down) {
            conn.sendMouseButtonDown(buttonIndex);
        }
        else {
            conn.sendMouseButtonUp(buttonIndex);
        }
    }

    @Override
    public void mouseVScroll(byte amount) {
        conn.sendMouseScroll(amount);
    }

    @Override
    public void mouseHScroll(byte amount) {
        conn.sendMouseHScroll(amount);
    }

    @Override
    public void keyboardEvent(boolean buttonDown, short keyCode) {
        short keyMap = keyboardTranslator.translate(keyCode, -1);
        if (keyMap != 0) {
            // handleSpecialKeys() takes the Android keycode
            if (handleSpecialKeys(keyCode, buttonDown)) {
                return;
            }

            if (buttonDown) {
                sendTrackedKeyboardInput(keyMap, KeyboardPacket.KEY_DOWN, getModifierState(), (byte)0);
            }
            else {
                sendTrackedKeyboardInput(keyMap, KeyboardPacket.KEY_UP, getModifierState(), (byte)0);
            }
        }
    }

    @Override
    public void onSystemUiVisibilityChange(int visibility) {
        // Don't do anything if we're not connected
        if (!connected) {
            return;
        }

        // This flag is set for all devices
        if ((visibility & View.SYSTEM_UI_FLAG_FULLSCREEN) == 0) {
            hideSystemUi(2000);
        }
        else if ((visibility & View.SYSTEM_UI_FLAG_HIDE_NAVIGATION) == 0) {
            hideSystemUi(2000);
        }
    }

    @Override
    public void onPerfUpdate(final String text) {
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                String mouseStats = mouseInputStats.takeSummary(android.os.SystemClock.uptimeMillis());
                String touchStats = touchInputStats.takeSummary(android.os.SystemClock.uptimeMillis())
                        .replace("Mouse input:", "Touch input:");
                String engineStats = buildEnginePerformanceStatus();
                performanceOverlayView.setText(text + engineStats +
                        (mouseStats.isEmpty() ? "" : "\n" + mouseStats) +
                        (touchStats.isEmpty() ? "" : "\n" + touchStats));
            }
        });
    }

    @Override
    public void onVideoThroughputSample(int receivedKbps, int lost, int frames, int rttMs, long sampledAtMs) {
        runOnUiThread(() -> {
            if (autoBitrateSession != null && connected && !streamPageStopped) {
                autoBitrateSession.recordThroughputSample(receivedKbps, sampledAtMs);
                if (conn == null || !conn.supportsPiuLinkControl()) return;
                int applied = conn.getAppliedLiveBitrate();
                int target = autoBitrateSession.nextEncoderTarget(applied, receivedKbps, lost, frames, rttMs, sampledAtMs);
                if (target > 0 && conn.setLiveBitrate(target))
                    LimeLog.info("Adaptive bitrate request " + applied + " -> " + target +
                            " Kbps, lost " + lost + "/" + frames + " frames, RTT " + rttMs + " ms");
            }
        });
    }

    private String buildEnginePerformanceStatus() {
        NvConnection connection = conn;
        int ceilingKbps = autoBitrateSession != null
                ? autoBitrateSession.getCeilingKbps() : prefConfig.bitrate;
        if (connection == null || !connection.supportsPiuLinkControl()) {
            return "\n" + getString(R.string.perf_compact_rate_unsupported,
                    ceilingKbps / 1000f);
        }

        int appliedKbps = connection.getAppliedLiveBitrate();
        String historyMbps = autoBitrateSession != null &&
                autoBitrateSession.hasHistoricalVideoThroughput()
                ? String.format(Locale.US, "%.2f Mbps",
                        autoBitrateSession.getHistoricalVideoKbps() / 1000f)
                : getString(R.string.perf_compact_collecting);
        int adaptiveState = connection.getContentAdaptiveState();
        String adaptive = adaptiveState == 2
                ? getString(R.string.perf_compact_adaptive_on)
                : adaptiveState == 1
                ? getString(R.string.perf_compact_adaptive_off)
                : adaptiveState == 0
                ? getString(R.string.perf_compact_adaptive_rejected)
                : contentAdaptiveControlTimedOut
                ? getString(R.string.perf_compact_adaptive_timeout)
                : getString(R.string.perf_compact_adaptive_pending);
        String hostTarget = appliedKbps > 0
                ? String.format(Locale.US, "%.1f Mbps", appliedKbps / 1000f)
                : getString(R.string.perf_compact_collecting);
        String networkMode = getString(autoBitrateSession == null ? R.string.perf_rate_manual :
                appliedKbps > 0 ? R.string.perf_rate_auto : R.string.perf_rate_startup_only);
        return "\n" + getString(R.string.perf_compact_rate,
                historyMbps, ceilingKbps / 1000f, hostTarget) +
                "\n" + getString(R.string.perf_compact_ceiling,
                networkMode, adaptive);
    }

    private String buildNetworkPathStatus() {
        NetworkPathClassifier.Path path = NetworkPathClassifier.classify(
                getIntent().getStringExtra(EXTRA_HOST));
        switch (path) {
            case IPV4_DIRECT:
                return getString(R.string.perf_overlay_network_path_ipv4);
            case IPV6_DIRECT:
                return getString(R.string.perf_overlay_network_path_ipv6);
            case TAILSCALE_MANAGED:
                return getString(R.string.perf_overlay_network_path_tailscale);
            default:
                return getString(R.string.perf_overlay_network_path_unknown);
        }
    }

    private String getActiveVideoCodecDescription() {
        int format = decoderRenderer != null ? decoderRenderer.getActiveVideoFormat() : 0;
        String codec;
        if ((format & MoonBridge.VIDEO_FORMAT_MASK_AV1) != 0) {
            codec = "AV1";
        }
        else if ((format & MoonBridge.VIDEO_FORMAT_MASK_H265) != 0) {
            codec = "HEVC/H.265";
        }
        else if ((format & MoonBridge.VIDEO_FORMAT_MASK_H264) != 0) {
            codec = "H.264/AVC";
        }
        else {
            codec = "Unknown";
        }
        if ((format & MoonBridge.VIDEO_FORMAT_MASK_10BIT) != 0) {
            codec += " 10-bit HDR";
        }
        return codec;
    }

    @Override
    public void onUsbPermissionPromptStarting() {
        // Disable PiP auto-enter while the USB permission prompt is on-screen. This prevents
        // us from entering PiP while the user is interacting with the OS permission dialog.
        suppressPipRefCount++;
        updatePipAutoEnter();
    }

    @Override
    public void onUsbPermissionPromptCompleted() {
        suppressPipRefCount--;
        updatePipAutoEnter();
    }

    @Override
    public boolean onKey(View view, int keyCode, KeyEvent keyEvent) {
        if (view == streamView && handleStreamOverlayNavigation(keyCode, keyEvent)) {
            return true;
        }
        switch (keyEvent.getAction()) {
            case KeyEvent.ACTION_DOWN:
                return handleKeyDown(keyEvent);
            case KeyEvent.ACTION_UP:
                return handleKeyUp(keyEvent);
            case KeyEvent.ACTION_MULTIPLE:
                return handleKeyMultiple(keyEvent);
            default:
                return false;
        }
    }
}
