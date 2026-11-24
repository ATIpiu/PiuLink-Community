package com.limelight.piulink;

import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Paths;
import static org.junit.Assert.*;

public class CompanionContractTest {
    @Test public void directionalSwitchUsesSignedWindowsCoordinatesAndSkipsUnknownGeometry() throws Exception {
        java.util.List<DisplayInventoryClient.Display> screens =
                DisplayInventoryClient.parseInventory(fixture().getJSONObject("inventory").toString()).displays;
        assertEquals(-1920, screens.get(0).bounds.x);
        assertEquals(120, screens.get(0).bounds.y);
        assertNull(screens.get(2).bounds);
        assertEquals(0, DesktopLayout.onlyOtherActive(screens, 1));
        assertEquals(1, DesktopLayout.onlyOtherActive(screens, 0));
        assertEquals(-1, DesktopLayout.onlyOtherActive(screens, 2));
        assertEquals(-1, DesktopLayout.onlyOtherActive(screens.subList(0, 1), 0));
        screens.get(2).bounds = new DesktopLayout.Bounds(1920, 0, 1280, 720);
        assertEquals(0, DesktopLayout.onlyOtherActive(screens, 1)); // inactive with remembered Windows coordinates
        DisplayInventoryClient.Display third = new DisplayInventoryClient.Display(3, "third", "Third", false, false, 1280, 720, false);
        third.bounds = screens.get(2).bounds;
        screens.add(third);
        assertEquals(-1, DesktopLayout.onlyOtherActive(screens, 1));
        screens.remove(third);
        screens.get(2).bounds = null;
        assertEquals(0, DesktopLayout.neighbor(screens, 1, DesktopLayout.Direction.LEFT));
        assertEquals(1, DesktopLayout.neighbor(screens, 0, DesktopLayout.Direction.RIGHT));
        assertEquals(-1, DesktopLayout.neighbor(screens, 2, DesktopLayout.Direction.RIGHT));
        screens.get(0).bounds = new DesktopLayout.Bounds(0, -1080, 1920, 1080);
        assertEquals(0, DesktopLayout.neighbor(screens, 1, DesktopLayout.Direction.UP));
        assertEquals(-1, DesktopLayout.neighbor(screens, 1, DesktopLayout.Direction.LEFT));
    }

    @Test public void unnamedPhysicalDisplayIsStillConnectable() throws Exception {
        JSONObject root = fixture().getJSONObject("inventory");
        JSONArray displays = root.getJSONArray("displays");
        JSONObject physical = displays.getJSONObject(0);
        physical.put("name", "");
        physical.put("windowsName", "DISPLAY1");
        root.put("displays", new JSONArray().put(physical));
        DisplayInventoryClient.Inventory inventory = DisplayInventoryClient.parseInventory(root.toString());
        assertEquals(1, inventory.displays.size());
        assertEquals("DISPLAY1", inventory.displays.get(0).name);
        assertNull(DisplayInventoryClient.defaultDisplay(inventory, "missing-vdd", false));
        physical.remove("windowsName");
        assertEquals("Display 1", DisplayInventoryClient.parseInventory(root.toString()).displays.get(0).name);
    }
    @Test public void missingExplicitTargetStopsAndAutomaticUsesPhysical() {
        DisplayInventoryClient.Display first = new DisplayInventoryClient.Display(0, "first", "Monitor", false, false, 1920, 1080, false);
        DisplayInventoryClient.Display primary = new DisplayInventoryClient.Display(1, "primary", "Monitor", true, false, 1920, 1080, false);
        DisplayInventoryClient.Inventory inventory = new DisplayInventoryClient.Inventory(true,
                java.util.Arrays.asList(first, primary), java.util.Collections.emptyList(), java.util.Collections.emptyList());
        assertNull(DisplayInventoryClient.defaultDisplay(inventory, "removed-vdd", false));
        assertSame(primary, DisplayInventoryClient.defaultDisplay(inventory, "", false));
        assertSame(first, DisplayInventoryClient.defaultDisplay(inventory, "FIRST", false));
        assertNull(DisplayInventoryClient.defaultDisplay(inventory, "", true));
    }
    @Test public void aNewConnectionUsesAnActiveDesktopWithoutApplyingAnIdleVirtualProfile() {
        DisplayInventoryClient.Display virtual = new DisplayInventoryClient.Display(0, "v", "VDD", false, true, 0, 0, true);
        DisplayInventoryClient.Display unplugged = new DisplayInventoryClient.Display(1, "old", "Screen", false, false, 0, 0, false);
        DisplayInventoryClient.Display current = new DisplayInventoryClient.Display(2, "active", "Screen", true, false, 1920, 1080, false);
        DisplayInventoryClient.Inventory inventory = new DisplayInventoryClient.Inventory(true,
                java.util.Arrays.asList(virtual, unplugged, current), java.util.Collections.emptyList(), java.util.Collections.emptyList());
        assertSame(current, DisplayInventoryClient.defaultDisplay(inventory, "", false));
        assertSame(virtual, DisplayInventoryClient.defaultDisplay(inventory, "v", false));
        assertSame(virtual, DisplayInventoryClient.defaultDisplay(inventory, "", true));
        assertNull(DisplayInventoryClient.defaultDisplay(inventory, "old", false));
    }
    @Test public void liveControlsRequireTheCapabilityAdvertisedByTheHost() throws Exception {
        JSONObject legacy = fixture().getJSONObject("inventory");
        legacy.remove("supportsSessionDisplays");
        assertFalse(DisplayInventoryClient.parseInventory(legacy.toString()).supportsSessionDisplays);
        legacy.put("supportsSessionDisplays", true);
        assertTrue(DisplayInventoryClient.parseInventory(legacy.toString()).supportsSessionDisplays);
    }
    private JSONObject fixture() throws Exception {
        return new JSONObject(new String(Files.readAllBytes(Paths.get(System.getProperty("piulink.companionContract"))), StandardCharsets.UTF_8));
    }
    @Test public void readsActualHostInventoryWithoutMergingSameNamedMonitors() throws Exception {
        DisplayInventoryClient.Inventory inventory = DisplayInventoryClient.parseInventory(fixture().getJSONObject("inventory").toString());
        assertEquals(3, inventory.displays.size());
        assertEquals(inventory.displays.get(0).name, inventory.displays.get(1).name);
        assertNotEquals(inventory.displays.get(0).deviceId, inventory.displays.get(1).deviceId);
        assertTrue(inventory.displays.get(2).virtual);
        assertTrue(inventory.displays.get(2).selected);
        assertEquals(0, inventory.displays.get(2).width); // An inactive VDD publishes null dimensions.
        assertTrue(inventory.exclusive);
        assertTrue(inventory.virtualRefreshRates.contains(120));
        assertTrue(inventory.supportsDisplayScaling);
        assertTrue(inventory.displays.get(2).scaleOptions.isEmpty()); // No fabricated DPI choices for an unreadable target
    }
    @Test public void producesRequestsAcceptedByHost() throws Exception {
        JSONArray requests = fixture().getJSONArray("selectionRequests");
        for (int i = 0; i < requests.length(); i++) {
            JSONObject expected = requests.getJSONObject(i);
            JSONObject actual = DisplayInventoryClient.buildSelection(expected.isNull("deviceId") ? null : expected.getString("deviceId"),
                    expected.getBoolean("exclusive"), expected.optInt("width"), expected.optInt("height"));
            assertEquals(expected.length(), actual.length());
            java.util.Iterator<String> keys = expected.keys();
            while (keys.hasNext()) {
                String key = keys.next();
                assertEquals(expected.get(key), actual.get(key));
            }
        }
        DisplayInventoryClient.validateSelectionResponse(fixture().getJSONObject("ready").toString());
    }
    @Test public void rejectsFalseSuccessAndInvalidDimensions() throws Exception {
        for (String reply : new String[] { "{}", "{\"status\":\"error\"}", "<html>login</html>" }) {
            try { DisplayInventoryClient.validateSelectionResponse(reply); fail("False readiness accepted"); }
            catch (java.io.IOException | org.json.JSONException expected) { }
        }
        try { DisplayInventoryClient.buildSelection(null, false, 1920, 0); fail("Partial resolution accepted"); }
        catch (java.io.IOException expected) { }
        JSONObject scaled = DisplayInventoryClient.buildSelection(
                "33333333-3333-3333-3333-333333333333", true, 1920, 1080, 150);
        assertEquals(150, scaled.getInt("scalePercent"));
        try { DisplayInventoryClient.buildSelection("display", false, 0, 0, 110); fail("Invalid scale accepted"); }
        catch (java.io.IOException expected) { }
    }
}
