// Adapts Sunshine's virtual input events to the private Xvfb session.
#define _GNU_SOURCE
#include <X11/Xlib.h>
#include <X11/extensions/XTest.h>
#include <dlfcn.h>
#include <errno.h>
#include <libevdev/libevdev.h>
#include <libevdev/libevdev-uinput.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

enum device_kind { keyboard = 1, mouse = 2, absolute = 4, touch = 8 };

struct libevdev_uinput {
    enum device_kind kind;
    int dx, dy, x, y, max_x, max_y;
    int wheel, horizontal, wheel_remainder, horizontal_remainder;
    int slot, primary_slot;
    bool moved, touch_down;
    bool keys[256], buttons[10];
};

static bool enabled;
static Display *display;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static unsigned ready_devices;

static void __attribute__((constructor)) initialize(void) {
    const char *screen = getenv("PIULINK_X11_INPUT_DISPLAY");
    const char *mode = getenv("PIULINK_SESSION_MODE");
    const char *current = getenv("DISPLAY");
    char executable[4096];
    ssize_t size = readlink("/proc/self/exe", executable, sizeof(executable) - 1);
    if (size <= 0 || !screen || !current || !mode || strcmp(mode, "headless") ||
        screen[0] != ':' || strspn(screen + 1, "0123456789") != strlen(screen + 1) ||
        !screen[1] || strcmp(screen, current)) return;
    executable[size] = '\0';
    const char *name = strrchr(executable, '/');
    if (!name || strcmp(name + 1, "sunshine")) return;
    enabled = XInitThreads() != 0;
}

static enum device_kind classify(const struct libevdev *dev) {
    const char *name = libevdev_get_name(dev);
    if (!name) return 0;
    if (!strncmp(name, "Keyboard passthrough", strlen("Keyboard passthrough"))) return keyboard;
    if (!strncmp(name, "Mouse passthrough", strlen("Mouse passthrough")))
        return libevdev_has_event_code(dev, EV_ABS, ABS_X) ? absolute : mouse;
    if (!strncmp(name, "Touch passthrough", strlen("Touch passthrough"))) return touch;
    return 0;
}

static bool open_display(void) {
    if (display) return true;
    Display *connection = XOpenDisplay(getenv("PIULINK_X11_INPUT_DISPLAY"));
    if (!connection) return false;
    int base, error, major, minor;
    if (!XTestQueryExtension(connection, &base, &error, &major, &minor)) {
        XCloseDisplay(connection);
        return false;
    }
    display = connection;
    return true;
}

static void mark_ready(enum device_kind kind) {
    const unsigned required = keyboard | mouse | absolute;
    if ((ready_devices & required) == required) return;
    ready_devices |= kind;
    if ((ready_devices & required) != required) return;
    const char *path = getenv("PIULINK_X11_INPUT_READY");
    FILE *status = path ? fopen(path, "w") : NULL;
    if (status) {
        fprintf(status, "%ld\n", (long)getpid());
        fclose(status);
    }
    fprintf(stderr, "PiuLink X11 input ready on %s (keyboard and mouse)\n", DisplayString(display));
}

int libevdev_uinput_create_from_device(const struct libevdev *dev, int fd, struct libevdev_uinput **result) {
    if (!enabled) {
        int (*next)(const struct libevdev *, int, struct libevdev_uinput **) =
            dlsym(RTLD_NEXT, "libevdev_uinput_create_from_device");
        return next ? next(dev, fd, result) : -ENOSYS;
    }
    enum device_kind kind = classify(dev);
    if (!kind) return -ENOTSUP;
    struct libevdev_uinput *device = calloc(1, sizeof(*device));
    if (!device) return -ENOMEM;
    device->kind = kind;
    device->max_x = libevdev_get_abs_maximum(dev, ABS_X);
    device->max_y = libevdev_get_abs_maximum(dev, ABS_Y);
    device->primary_slot = -1;

    pthread_mutex_lock(&lock);
    bool available = open_display();
    if (available) mark_ready(kind);
    pthread_mutex_unlock(&lock);
    if (!available) {
        free(device);
        return -ENODEV;
    }
    *result = device;
    return 0;
}

static int x_button(unsigned code) {
    switch (code) {
        case BTN_LEFT: return 1;
        case BTN_MIDDLE: return 2;
        case BTN_RIGHT: return 3;
        case BTN_SIDE: return 8;
        case BTN_EXTRA: return 9;
        default: return 0;
    }
}

static void button(struct libevdev_uinput *device, int code, bool down) {
    if (!code || device->buttons[code] == down) return;
    XTestFakeButtonEvent(display, code, down, CurrentTime);
    device->buttons[code] = down;
}

static void scroll(int amount, int positive, int negative, int *remainder) {
    long total = (long)*remainder + amount;
    *remainder = (int)(total % 120);
    long steps = total / 120;
    int code = steps >= 0 ? positive : negative;
    for (long i = 0; i < labs(steps) && i < 32; i++) {
        XTestFakeButtonEvent(display, code, True, CurrentTime);
        XTestFakeButtonEvent(display, code, False, CurrentTime);
    }
}

static int pixels(int value, int maximum, int dimension) {
    if (value <= 0 || maximum <= 0) return 0;
    if (value >= maximum) return dimension - 1;
    return (int)((long)value * dimension / maximum);
}

static void flush(struct libevdev_uinput *device) {
    if (device->dx || device->dy) {
        XTestFakeRelativeMotionEvent(display, device->dx, device->dy, CurrentTime);
        device->dx = device->dy = 0;
    }
    if (device->moved) {
        int screen = DefaultScreen(display);
        XTestFakeMotionEvent(display, screen,
            pixels(device->x, device->max_x, DisplayWidth(display, screen)),
            pixels(device->y, device->max_y, DisplayHeight(display, screen)), CurrentTime);
        device->moved = false;
    }
    if (device->kind == touch) {
        button(device, 1, device->touch_down);
        if (!device->touch_down) device->primary_slot = -1;
    }
    scroll(device->wheel, 4, 5, &device->wheel_remainder);
    scroll(device->horizontal, 7, 6, &device->horizontal_remainder);
    device->wheel = device->horizontal = 0;
    XFlush(display);
}

static void absolute_event(struct libevdev_uinput *device, unsigned code, int value) {
    if (device->kind == touch) {
        if (code == ABS_MT_SLOT) device->slot = value;
        if (code == ABS_MT_TRACKING_ID && value >= 0 && device->primary_slot < 0) {
            device->primary_slot = device->slot;
            device->touch_down = true;
        }
        if (device->slot != device->primary_slot) return;
        if (code == ABS_MT_TRACKING_ID && value < 0) device->touch_down = false;
    }
    if (code == ABS_X) { device->x = value; device->moved = true; }
    if (code == ABS_Y) { device->y = value; device->moved = true; }
}

int libevdev_uinput_write_event(const struct libevdev_uinput *handle, unsigned type, unsigned code, int value) {
    if (!enabled) {
        int (*next)(const struct libevdev_uinput *, unsigned, unsigned, int) =
            dlsym(RTLD_NEXT, "libevdev_uinput_write_event");
        return next ? next(handle, type, code, value) : -ENOSYS;
    }
    struct libevdev_uinput *device = (struct libevdev_uinput *)handle;
    pthread_mutex_lock(&lock);
    if (type == EV_KEY && device->kind == keyboard && code <= 247) {
        unsigned key = code + 8;
        bool down = value != 0;
        // XKB supplies repeat. Forwarding the driver's synthetic repeated down events would double it.
        if (device->keys[key] != down) {
            XTestFakeKeyEvent(display, key, down, CurrentTime);
            device->keys[key] = down;
        }
    } else if (type == EV_KEY && device->kind == mouse) {
        button(device, x_button(code), value != 0);
    } else if (type == EV_REL) {
        if (code == REL_X) device->dx = value;
        if (code == REL_Y) device->dy = value;
        if (code == REL_WHEEL_HI_RES) device->wheel = value;
        if (code == REL_HWHEEL_HI_RES) device->horizontal = value;
    } else if (type == EV_ABS) {
        absolute_event(device, code, value);
    } else if (type == EV_SYN && code == SYN_REPORT) {
        flush(device);
    }
    pthread_mutex_unlock(&lock);
    return 0;
}

void libevdev_uinput_destroy(struct libevdev_uinput *device) {
    if (!enabled) {
        void (*next)(struct libevdev_uinput *) = dlsym(RTLD_NEXT, "libevdev_uinput_destroy");
        if (next) next(device);
        return;
    }
    if (!device) return;
    pthread_mutex_lock(&lock);
    for (unsigned i = 0; i < sizeof(device->keys); i++) {
        if (device->keys[i]) XTestFakeKeyEvent(display, i, False, CurrentTime);
    }
    for (unsigned i = 1; i < sizeof(device->buttons); i++) {
        if (device->buttons[i]) XTestFakeButtonEvent(display, i, False, CurrentTime);
    }
    XFlush(display);
    pthread_mutex_unlock(&lock);
    free(device);
}

int libevdev_uinput_get_fd(const struct libevdev_uinput *device) {
    if (enabled) return -1;
    int (*next)(const struct libevdev_uinput *) = dlsym(RTLD_NEXT, "libevdev_uinput_get_fd");
    return next ? next(device) : -1;
}

const char *libevdev_uinput_get_devnode(struct libevdev_uinput *device) {
    if (enabled) return "";
    const char *(*next)(struct libevdev_uinput *) = dlsym(RTLD_NEXT, "libevdev_uinput_get_devnode");
    return next ? next(device) : NULL;
}

const char *libevdev_uinput_get_syspath(struct libevdev_uinput *device) {
    if (enabled) return "";
    const char *(*next)(struct libevdev_uinput *) = dlsym(RTLD_NEXT, "libevdev_uinput_get_syspath");
    return next ? next(device) : NULL;
}
