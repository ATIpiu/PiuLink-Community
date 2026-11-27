package com.limelight.piulink;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertTrue;

import android.widget.Spinner;

import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import androidx.test.uiautomator.By;
import androidx.test.uiautomator.UiDevice;
import androidx.test.uiautomator.UiObject2;
import androidx.test.uiautomator.Until;

import org.junit.Test;
import org.junit.runner.RunWith;

/** Real-device regression for stream overlays and the monitor picker. */
@RunWith(AndroidJUnit4.class)
public final class StreamOverlayDeviceTest {
    private static final String RELEASE_PACKAGE = "labs.atipiu.piulink";
    private static final long TIMEOUT = 15_000;

    @Test(timeout = 90_000)
    public void overlaysDismissOutsideAndVirtualModeControlsStayInline() throws Exception {
        UiDevice device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation());
        device.executeShellCommand("am force-stop " + RELEASE_PACKAGE);
        device.executeShellCommand("monkey -p " + RELEASE_PACKAGE +
                " -c android.intent.category.LAUNCHER 1");

        UiObject2 computer = device.wait(Until.findObject(By.textContains(androidx.test.platform.app.InstrumentationRegistry.getArguments().getString("piulinkTestHost", "PiuLink-QA"))), TIMEOUT);
        assertNotNull("Saved test Host was not visible", computer);
        computer.click();
        UiObject2 desktop = device.wait(Until.findObject(By.desc("Desktop")), TIMEOUT);
        assertNotNull("Desktop application was not visible", desktop);
        desktop.click();

        UiObject2 rightHandle = device.wait(Until.findObject(
                By.res(RELEASE_PACKAGE, "shortcutRailHandle")), TIMEOUT);
        assertNotNull("Stream did not reach the shortcut rail", rightHandle);
        rightHandle.click();
        assertNotNull("Alt + Tab must be a horizontal shortcut label",
                device.wait(Until.findObject(By.textContains("Alt + Tab")), 3_000));
        device.click(device.getDisplayWidth() / 2, device.getDisplayHeight() / 2);
        assertTrue("Outside tap did not close the shortcut rail",
                device.wait(Until.gone(By.textContains("Alt + Tab")), 3_000));

        UiObject2 leftHandle = device.findObject(By.res(RELEASE_PACKAGE, "streamMenuHandle"));
        assertNotNull(leftHandle);
        leftHandle.click();
        assertNotNull("Left stream menu did not open",
                device.wait(Until.findObject(By.textContains("串流操作")), 3_000));
        UiObject2 mic = device.wait(Until.findObject(By.desc("开启远端麦克风")), 3_000);
        assertNotNull("The remote microphone must start off and be reachable before scrolling", mic);
        UiObject2 menuScroll = device.findObject(By.res(RELEASE_PACKAGE, "streamMenuScroll"));
        assertNotNull(menuScroll);
        for (int i = 0; i < 5 && !device.hasObject(By.desc("断开连接")); i++)
            menuScroll.scroll(androidx.test.uiautomator.Direction.DOWN, 0.8f);
        assertNotNull("Disconnect must be reachable on a short landscape screen", device.findObject(By.desc("断开连接")));
        device.click(device.getDisplayWidth() / 2, device.getDisplayHeight() / 2);
        assertTrue("Outside tap did not close the stream menu",
                device.wait(Until.gone(By.textContains("串流操作")), 3_000));

        device.findObject(By.res(RELEASE_PACKAGE, "streamMenuHandle")).click();
        UiObject2 monitor = device.wait(Until.findObject(By.desc("屏幕设置")), 3_000);
        assertNotNull("Display settings action was not visible in the stream menu", monitor);
        monitor.click();
        UiObject2 virtual = device.wait(Until.findObject(By.textContains("集中所有窗口")), TIMEOUT);
        assertNotNull("Virtual display row was not visible", virtual);
        virtual.click();
        assertEquals("Virtual target must show resolution and frame-rate selectors inline", 2,
                device.findObjects(By.clazz(Spinner.class)).size());
    }
}
