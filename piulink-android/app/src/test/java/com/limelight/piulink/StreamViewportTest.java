package com.limelight.piulink;

import org.junit.Test;
import static org.junit.Assert.*;

public class StreamViewportTest {
    @Test public void localMotionPansBeforeNextHostSampleAndAnimationUsesFrameTime() {
        StreamViewport v = new StreamViewport();
        v.layout(1000,600,0,0,1000,600,false);
        v.pinch(2,500,300);
        assertFalse(v.movePointer(.1f,0));
        v.position(.65f,.5f,false);
        float start = v.x;
        assertTrue(v.movePointer(.1f,0));
        assertTrue(v.x < start);
        float first = StreamViewport.approach(start,v.x,8,24);
        assertTrue(first < start && first > v.x);
        assertEquals(StreamViewport.approach(start,v.x,16,24), StreamViewport.approach(first,v.x,8,24), .001f);
        v.layout(1000,600,0,0,1000,300,true);
        assertFalse(v.movePointer(.1f,0));
    }
    @Test public void caretMovesWithLinesAndKeyboardHeightThenRestoresZoom() {
        StreamViewport v = new StreamViewport();
        v.layout(1200,800,0,0,1200,800,false);
        v.pinch(2f,300,600);
        float x = v.x, y = v.y;
        v.layout(1200,800,0,0,1200,400,true);
        assertTrue(v.scale*800+v.y <= 400);
        v.position(.5f,.92f,true);
        float caret = .92f*800*v.scale+v.y;
        assertTrue(caret >= 0 && caret <= 350);
        v.position(.5f,.1f,true);
        v.layout(1200,800,0,0,1200,300,true);
        caret = .1f*800*v.scale+v.y;
        assertTrue(caret >= 0 && caret < 300);
        v.layout(1200,800,0,0,1200,800,false);
        assertEquals(2f,v.scale,0); assertEquals(x,v.x,0); assertEquals(y,v.y,0);
    }

    @Test public void zoomStaysAtFingersThenFollowsPointerAtEachEdge() {
        StreamViewport v = new StreamViewport();
        v.layout(1000,600,0,-100,1000,700,false);
        v.pinch(2f,300,200);
        assertEquals(300,300*v.scale+v.x,0);
        v.position(.3f,.4f,false);
        v.position(.97f,.98f,false);
        assertTrue(.97f*2000+v.x<=1000 && .98f*1200+v.y<=700);
        v.position(.02f,.02f,false);
        assertTrue(.02f*2000+v.x>=0 && .02f*1200+v.y>=-100);
        v.pinch(.1f,300,200);
        assertEquals(1f,v.scale,0); assertEquals(0f,v.x,0); assertEquals(0f,v.y,0);
    }
}
