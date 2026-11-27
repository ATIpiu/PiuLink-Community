package com.limelight.piulink;

/** All coordinates are relative to the original, untransformed video origin. */
public final class StreamViewport {
    private float width, height, left, top, right, bottom;
    private float zoom = 1f, manualX, manualY;
    private float caretX = Float.NaN, caretY, pointerX = Float.NaN, pointerY;
    private boolean keyboard, centerCaret;
    public float scale = 1f, x, y;

    public boolean isZoomed() { return zoom > 1f; }

    public void captureChanged() {
        pointerX = caretX = Float.NaN;
        centerCaret = true;
        manualX = (left + right - width * zoom) / 2;
        manualY = (top + bottom - height * zoom) / 2;
        update();
    }

    public boolean movePointer(float dx, float dy) {
        if (keyboard || !isZoomed() || Float.isNaN(pointerX) || dx == 0 && dy == 0) return false;
        position(Math.max(0f, Math.min(1f, pointerX + dx)),
                Math.max(0f, Math.min(1f, pointerY + dy)), false);
        return true;
    }

    // Exponential approach uses frame time, so 60 Hz and 120 Hz take the same time to settle.
    public static float approach(float shown, float target, float elapsedMs, float responseMs) {
        float remaining = target - shown;
        if (Math.abs(remaining) <= 0.4f) return target;
        return target - remaining * (float)Math.exp(-elapsedMs / responseMs);
    }

    public void layout(float w, float h, float l, float t, float r, float b, boolean ime) {
        if (w <= 0 || h <= 0 || r <= l || b <= t) return;
        if (keyboard != ime) {
            caretX = pointerX = Float.NaN;
            centerCaret = true;
        }
        if (width != w || height != h || bottom != b) centerCaret = true;
        width = w; height = h; left = l; top = t; right = r; bottom = b; keyboard = ime;
        update();
    }

    public void pinch(float factor, float anchorX, float anchorY) {
        if (width <= 0 || factor <= 0 || !Float.isFinite(factor)) return;
        float next = Math.max(1f, Math.min(4f, zoom * factor));
        manualX = anchorX - (anchorX - x) * next / scale;
        manualY = anchorY - (anchorY - y) * next / scale;
        zoom = next;
        if (zoom == 1f) { manualX = manualY = 0; pointerX = Float.NaN; }
        update();
    }

    public void position(float nx, float ny, boolean caret) {
        if (caret != keyboard || width <= 0) return;
        if (!(nx >= 0 && nx <= 1 && ny >= 0 && ny <= 1)) {
            if (caret) { caretX = Float.NaN; centerCaret = true; update(); }
            else pointerX = Float.NaN;
            return;
        }
        if (caret) {
            if (Float.isNaN(caretX)) centerCaret = true;
            caretX = nx; caretY = ny;
        } else {
            if (isZoomed() && !Float.isNaN(pointerX) && (pointerX != nx || pointerY != ny)) {
                manualX = follow(x, nx * width * zoom, left, right, false);
                manualY = follow(y, ny * height * zoom, top, bottom, false);
            }
            pointerX = nx; pointerY = ny;
        }
        update();
    }

    private void update() {
        if (width <= 0) return;
        if (keyboard && !Float.isFinite(caretX)) {
            // A custom editor or older Host may not expose its caret. Keep every line visible.
            scale = Math.min(1f, Math.min((right-left)/width, (bottom-top)/height));
            x = (left+right-width*scale)/2; y = (top+bottom-height*scale)/2;
        } else if (keyboard) {
            scale = Math.max(1.35f, zoom);
            x = bound(follow(x, caretX*width*scale, left, right, centerCaret), width*scale, left, right);
            y = bound(follow(y, caretY*height*scale, top, bottom, centerCaret), height*scale, top, bottom);
            centerCaret = false;
        } else {
            scale = zoom;
            x = manualX = bound(manualX, width*scale, left, right);
            y = manualY = bound(manualY, height*scale, top, bottom);
        }
    }

    private static float follow(float offset, float point, float start, float end, boolean center) {
        if (center) return (start+end)/2-point;
        float margin = (end-start)*0.14f;
        return Math.max(start+margin-point, Math.min(end-margin-point, offset));
    }

    private static float bound(float offset, float size, float start, float end) {
        if (size <= end-start) return (start+end-size)/2;
        return Math.max(end-size, Math.min(start, offset));
    }
}
