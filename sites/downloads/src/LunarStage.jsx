import React, { useEffect, useRef } from "react";
import { createMoonRenderer } from "./moonRenderer.js";

const TAU = Math.PI * 2;
const tracks = [
  { rx: 302, ry: 84, angle: -20, period: 10.8, phase: 0.23 },
  { rx: 264, ry: 115, angle: -37, period: 15.4, phase: 0.68 },
];

function orbitPoint(track, progress) {
  const a = progress * TAU;
  const tilt = (track.angle * Math.PI) / 180;
  const x = track.rx * Math.cos(a),
    y = track.ry * Math.sin(a);
  return [
    340 + x * Math.cos(tilt) - y * Math.sin(tilt),
    212 + x * Math.sin(tilt) + y * Math.cos(tilt),
  ];
}

function ArcLayer({ front }) {
  return (
    <svg
      className={"arc-layer " + (front ? "front" : "back")}
      viewBox="0 0 680 424"
      aria-hidden="true"
    >
      {tracks.map((t, i) => {
        const start = front ? 0 : 0.5;
        const dots = Array.from({ length: 90 }, (_, n) =>
          orbitPoint(t, start + n / 178).join(","),
        ).join(" ");
        return (
          <g key={i}>
            <polyline className={`orbit-line orbit-${i}`} points={dots} />
            <g className="orbiter" data-track={i} data-front={front}>
              <polyline className="ion-trail" />
              <circle className="ion-halo" r={i ? 4 : 6} />
              <circle className="ion-core" r={i ? 1.3 : 2} />
            </g>
          </g>
        );
      })}
    </svg>
  );
}

const orientations = {
  windows: 0,
  android: 0.072,
  harmony: 0.072,
  macos: -0.068,
  ios: -0.068,
  linux: 0.135,
};

export function LunarStage({ selected, paused }) {
  const root = useRef(null),
    target = useRef(orientations[selected]),
    stop = useRef(paused),
    repaint = useRef(null);
  useEffect(() => {
    target.current = orientations[selected];
    repaint.current?.();
  }, [selected]);
  useEffect(() => {
    stop.current = paused;
    repaint.current?.();
  }, [paused]);
  useEffect(() => {
    const stage = root.current,
      globe = stage.querySelector("canvas"),
      field = stage.querySelector(".orbital-field");
    const probes = [...stage.querySelectorAll(".orbiter")].map((group) => ({
      group,
      trail: group.firstElementChild,
      lights: [...group.querySelectorAll("circle")],
      track: tracks[Number(group.dataset.track)],
      front: group.dataset.front === "true",
    }));
    const preference = matchMedia("(prefers-reduced-motion: reduce)");
    let alive = true,
      visible = true,
      renderer,
      frame = 0,
      last = 0,
      elapsed = 0;
    let px = 0,
      py = 0,
      tx = 0,
      ty = 0,
      spin = 0.012,
      drag = 0,
      dragGoal = 0,
      pressed = null,
      startX = 0,
      orientation = target.current;
    let globeTime = -1;
    function render(now) {
      frame = 0;
      if (!visible || document.hidden || !alive) return;
      const dt = Math.min((now - (last || now)) / 1000, 0.05);
      const still = preference.matches || stop.current;
      last = now;
      if (!still) {
        elapsed += dt;
        spin += dt * 0.006;
      }
      const k = still ? 1 : 1 - Math.exp(-6 * dt);
      px += (tx - px) * k;
      py += (ty - py) * k;
      drag += (dragGoal - drag) * k;
      orientation += (target.current - orientation) * k;
      if (
        renderer &&
        (still ||
          pressed !== null ||
          now - globeTime >= 1000 / 60 - 1 ||
          globeTime < 0)
      ) {
        renderer.draw(spin + drag + orientation + px * 0.026, 0.045 + py * 0.1);
        globeTime = now;
        globe.classList.add("ready");
      }
      field.style.transform = `translate3d(${px * 12}px,${py * 8 + Math.sin(elapsed * 0.48) * 2.8}px,0) rotate(${px * 1.6 + orientation * 15}deg)`;
      for (const { group, track, front, trail, lights } of probes) {
        const progress = (elapsed / track.period + track.phase) % 1;
        const show = progress < 0.5 === front;
        group.style.visibility = show ? "visible" : "hidden";
        if (!show) continue;
        const [x, y] = orbitPoint(track, progress);
        lights.forEach((light) => {
          light.setAttribute("cx", x);
          light.setAttribute("cy", y);
        });
        const path = Array.from({ length: 12 }, (_, n) =>
          orbitPoint(track, progress - (11 - n) * 0.00165).join(","),
        ).join(" ");
        trail.setAttribute("points", path);
      }
      if (!still) frame = requestAnimationFrame(render);
    }
    function wake() {
      if (alive && visible && !document.hidden && !frame)
        frame = requestAnimationFrame(render);
    }
    function resetClock() {
      last = 0;
      if (frame) cancelAnimationFrame(frame);
      frame = 0;
      wake();
    }
    function move(event) {
      if (preference.matches || stop.current) return;
      const bounds = stage.getBoundingClientRect();
      tx = Math.max(
        -1,
        Math.min(1, ((event.clientX - bounds.left) / bounds.width) * 2 - 1),
      );
      ty = Math.max(
        -1,
        Math.min(1, ((event.clientY - bounds.top) / bounds.height) * 2 - 1),
      );
      if (pressed === event.pointerId) {
        dragGoal += ((event.clientX - startX) / bounds.width) * 0.75;
        startX = event.clientX;
      }
    }
    function down(event) {
      if (
        preference.matches ||
        stop.current ||
        !event.isPrimary ||
        event.button !== 0
      )
        return;
      pressed = event.pointerId;
      startX = event.clientX;
      stage.setPointerCapture(pressed);
    }
    function end() {
      pressed = null;
      tx = 0;
      ty = 0;
    }
    function leave() {
      if (pressed === null) {
        tx = 0;
        ty = 0;
      }
    }
    const observer = new IntersectionObserver(([entry]) => {
      visible = entry.isIntersecting;
      resetClock();
    });
    observer.observe(stage);
    const size = new ResizeObserver(resetClock);
    size.observe(stage);
    document.addEventListener("visibilitychange", resetClock);
    preference.addEventListener("change", resetClock);
    stage.addEventListener("pointermove", move);
    stage.addEventListener("pointerdown", down);
    stage.addEventListener("lostpointercapture", end);
    stage.addEventListener("pointerleave", leave);
    createMoonRenderer(globe, import.meta.env.BASE_URL).then((result) => {
      if (!alive) {
        result?.dispose();
        return;
      }
      renderer = result;
      resetClock();
    });
    repaint.current = resetClock;
    wake();
    return () => {
      alive = false;
      repaint.current = null;
      cancelAnimationFrame(frame);
      observer.disconnect();
      size.disconnect();
      renderer?.dispose();
      preference.removeEventListener("change", resetClock);
      document.removeEventListener("visibilitychange", resetClock);
      stage.removeEventListener("pointermove", move);
      stage.removeEventListener("pointerdown", down);
      stage.removeEventListener("lostpointercapture", end);
      stage.removeEventListener("pointerleave", leave);
    };
  }, []);
  return (
    <div className="lunar-stage" ref={root} aria-hidden="true">
      <div className="orbital-field">
        <ArcLayer front={false} />
        <div className="globe-shadow" />
        <div className="planet">
          <img
            src={import.meta.env.BASE_URL + "moon-poster.webp"}
            alt=""
            fetchPriority="high"
          />
          <canvas />
        </div>
        <ArcLayer front />
      </div>
    </div>
  );
}
