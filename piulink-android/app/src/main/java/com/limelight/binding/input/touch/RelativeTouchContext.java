package com.limelight.binding.input.touch;

import android.os.Handler;
import android.os.Looper;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.ViewConfiguration;

import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.input.MouseButtonPacket;
import com.limelight.preferences.PreferenceConfiguration;

public class RelativeTouchContext implements TouchContext {
    private int lastTouchX = 0;
    private int lastTouchY = 0;
    private int originalTouchX = 0;
    private int originalTouchY = 0;
    private long originalTouchTime = 0;
    private boolean cancelled;
    private boolean confirmedMove;
    private boolean confirmedDrag;
    private boolean confirmedScroll;
    private double xFactor, yFactor;
    private final TouchpadDelta pointerX = new TouchpadDelta();
    private final TouchpadDelta pointerY = new TouchpadDelta();
    private int pointerCount;
    private int maxPointerCountInGesture;

    private final NvConnection conn;
    private final int actionIndex;
    private final int referenceWidth;
    private final int referenceHeight;
    private final View targetView;
    private final PreferenceConfiguration prefConfig;
    private final Handler handler;
    private final Runnable onPrimaryTap;
    private final int tapMovementThreshold;

    private final Runnable dragTimerRunnable = new Runnable() {
        @Override
        public void run() {
            // Check if someone already set move
            if (confirmedMove) {
                return;
            }

            // The drag should only be processed for the primary finger
            if (actionIndex != maxPointerCountInGesture - 1) {
                return;
            }

            // We haven't been cancelled before the timer expired so begin dragging
            confirmedDrag = true;
            conn.sendMouseButtonDown(getMouseButtonIndex());
        }
    };

    // Indexed by MouseButtonPacket.BUTTON_XXX - 1
    private final Runnable[] buttonUpRunnables = new Runnable[] {
            new Runnable() {
                @Override
                public void run() {
                    conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_LEFT);
                }
            },
            new Runnable() {
                @Override
                public void run() {
                    conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_MIDDLE);
                }
            },
            new Runnable() {
                @Override
                public void run() {
                    conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_RIGHT);
                }
            },
            new Runnable() {
                @Override
                public void run() {
                    conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_X1);
                }
            },
            new Runnable() {
                @Override
                public void run() {
                    conn.sendMouseButtonUp(MouseButtonPacket.BUTTON_X2);
                }
            }
    };

    private static final int MINIMUM_TAP_MOVEMENT_THRESHOLD = 20;
    private static final int TAP_TIME_THRESHOLD = 400;
    private static final int DRAG_TIME_THRESHOLD = 650;

    private static final int SCROLL_SPEED_FACTOR = 5;

    public RelativeTouchContext(NvConnection conn, int actionIndex,
                                int referenceWidth, int referenceHeight,
                                View view, PreferenceConfiguration prefConfig, Runnable onPrimaryTap, MouseMotion onMouseMove)
    {
        this.conn = conn;
        this.actionIndex = actionIndex;
        this.referenceWidth = referenceWidth;
        this.referenceHeight = referenceHeight;
        this.targetView = view;
        this.prefConfig = prefConfig;
        this.onPrimaryTap = onPrimaryTap;
        this.onMouseMove = onMouseMove;
        this.handler = new Handler(Looper.getMainLooper());
        this.tapMovementThreshold = Math.max(
                MINIMUM_TAP_MOVEMENT_THRESHOLD,
                ViewConfiguration.get(view.getContext()).getScaledTouchSlop() * 2);
    }

    private final MouseMotion onMouseMove;

    @Override
    public int getActionIndex()
    {
        return actionIndex;
    }

    private boolean isWithinTapBounds(int touchX, int touchY)
    {
        int xDelta = Math.abs(touchX - originalTouchX);
        int yDelta = Math.abs(touchY - originalTouchY);
        return Math.hypot(xDelta, yDelta) <= tapMovementThreshold;
    }

    private TouchpadTapClassifier.Button classifyTap(long eventTime)
    {
        long timeDelta = eventTime - originalTouchTime;
        return TouchpadTapClassifier.classify(
                actionIndex,
                maxPointerCountInGesture,
                pointerCount,
                cancelled,
                confirmedMove,
                confirmedDrag,
                confirmedScroll,
                isWithinTapBounds(lastTouchX, lastTouchY),
                timeDelta,
                TAP_TIME_THRESHOLD);
    }

    private byte getMouseButtonIndex()
    {
        if (actionIndex == 1) {
            return MouseButtonPacket.BUTTON_RIGHT;
        }
        else {
            return MouseButtonPacket.BUTTON_LEFT;
        }
    }

    @Override
    public boolean touchDownEvent(int eventX, int eventY, long eventTime, boolean isNewFinger)
    {
        // Get the view dimensions to scale inputs on this touch
        xFactor = referenceWidth / (double)targetView.getWidth();
        yFactor = referenceHeight / (double)targetView.getHeight();

        pointerX.reset();
        pointerY.reset();
        originalTouchX = lastTouchX = eventX;
        originalTouchY = lastTouchY = eventY;

        if (isNewFinger) {
            maxPointerCountInGesture = pointerCount;
            originalTouchTime = eventTime;
            cancelled = confirmedDrag = confirmedMove = confirmedScroll = false;

            if (actionIndex == 0) {
                // Start the timer for engaging a drag
                startDragTimer();
            }
        }

        return true;
    }

    @Override
    public void touchUpEvent(int eventX, int eventY, long eventTime)
    {
        if (cancelled) {
            return;
        }

        // Cancel the drag timer
        cancelDragTimer();

        if (confirmedDrag) {
            // Raise the button after a drag
            conn.sendMouseButtonUp(getMouseButtonIndex());
        }
        else
        {
            TouchpadTapClassifier.Button tapButton = classifyTap(eventTime);
            if (tapButton == TouchpadTapClassifier.Button.NONE) {
                return;
            }

            byte buttonIndex = tapButton == TouchpadTapClassifier.Button.RIGHT
                    ? MouseButtonPacket.BUTTON_RIGHT
                    : MouseButtonPacket.BUTTON_LEFT;

            // Lower the mouse button
            conn.sendMouseButtonDown(buttonIndex);
            if (tapButton == TouchpadTapClassifier.Button.LEFT) onPrimaryTap.run();
            if (tapButton == TouchpadTapClassifier.Button.RIGHT) {
                targetView.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY);
            }

            // Release the mouse button in 100ms to allow for apps that use polling
            // to detect mouse button presses.
            Runnable buttonUpRunnable = buttonUpRunnables[buttonIndex - 1];
            handler.removeCallbacks(buttonUpRunnable);
            handler.postDelayed(buttonUpRunnable, 100);
        }
    }

    private void startDragTimer() {
        cancelDragTimer();
        handler.postDelayed(dragTimerRunnable, DRAG_TIME_THRESHOLD);
    }

    private void cancelDragTimer() {
        handler.removeCallbacks(dragTimerRunnable);
    }

    private void checkForConfirmedMove(int eventX, int eventY) {
        // If we've already confirmed something, get out now
        if (confirmedMove || confirmedDrag) {
            return;
        }

        // If it leaves the tap bounds before the drag time expires, it's a move.
        if (!isWithinTapBounds(eventX, eventY)) {
            confirmedMove = true;
            cancelDragTimer();
            return;
        }

    }

    private void checkForConfirmedScroll() {
        // Enter scrolling mode if we've already left the tap zone
        // and we have 2 fingers on screen. Leave scroll mode if
        // we no longer have 2 fingers on screen
        confirmedScroll = (actionIndex == 0 && pointerCount == 2 && confirmedMove);
    }

    @Override
    public boolean touchMoveEvent(int eventX, int eventY, long eventTime)
    {
        if (cancelled) {
            return true;
        }

        if (eventX != lastTouchX || eventY != lastTouchY)
        {
            checkForConfirmedMove(eventX, eventY);
            checkForConfirmedScroll();

            // We only send moves and drags for the primary touch point
            if (actionIndex == 0) {
                if (pointerCount == 2) {
                    int dy = eventY - lastTouchY;
                    int scroll = Integer.signum(dy) * (int) Math.round(Math.abs(dy) * yFactor);
                    if (confirmedScroll && scroll != 0) {
                        conn.sendMouseHighResScroll((short) (scroll * SCROLL_SPEED_FACTOR));
                    }
                    lastTouchX = eventX;
                    if (scroll != 0) lastTouchY = eventY;
                } else {
                    double speed = prefConfig.touchpadSpeedPercent / 100.0;
                    short dx = pointerX.move(eventX - lastTouchX, xFactor * speed);
                    short dy = pointerY.move(eventY - lastTouchY, yFactor * speed);
                    lastTouchX = eventX;
                    lastTouchY = eventY;
                    if (dx != 0 || dy != 0) {
                        onMouseMove.move(dx, dy);
                        if (prefConfig.absoluteMouseMode) {
                            conn.sendMouseMoveAsMousePosition(dx, dy,
                                    (short) targetView.getWidth(), (short) targetView.getHeight());
                        } else {
                            conn.sendMouseMove(dx, dy);
                        }
                    }
                }
            }
            else {
                lastTouchX = eventX;
                lastTouchY = eventY;
            }
        }

        return true;
    }

    @Override
    public void cancelTouch() {
        cancelled = true;

        // Cancel the drag timer
        cancelDragTimer();

        // If it was a confirmed drag, we'll need to raise the button now
        if (confirmedDrag) {
            conn.sendMouseButtonUp(getMouseButtonIndex());
        }
    }

    @Override
    public boolean isCancelled() {
        return cancelled;
    }

    @Override
    public void setPointerCount(int pointerCount) {
        if (this.pointerCount != pointerCount) {
            pointerX.reset();
            pointerY.reset();
        }
        this.pointerCount = pointerCount;

        if (pointerCount > maxPointerCountInGesture) {
            maxPointerCountInGesture = pointerCount;
        }
    }
}
