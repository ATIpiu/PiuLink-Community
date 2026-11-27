import gsap from "gsap";
import { ScrollTrigger } from "gsap/ScrollTrigger";
import { useGSAP } from "@gsap/react";

gsap.registerPlugin(ScrollTrigger, useGSAP);

export function useSceneMotion(scope) {
  useGSAP(
    () => {
      const media = gsap.matchMedia();
      media.add("(prefers-reduced-motion: no-preference)", () => {
        const intro = gsap.timeline({ defaults: { ease: "power3.out" } });
        intro
          .from(
            ".masthead .brand, .masthead nav",
            { y: -10, opacity: 0, stagger: 0.075, duration: 0.55 },
            0,
          )
          .from(
            ".lunar-stage",
            { scale: 0.94, y: 14, opacity: 0, duration: 1.25 },
            0,
          )
          .from(
            ".title-word",
            {
              yPercent: 17,
              rotate: -2,
              autoAlpha: 0,
              stagger: 0.11,
              duration: 1.05,
            },
            0.075,
          )
          .from(
            ".hero-description, .hero-actions",
            { opacity: 0, y: 13, stagger: 0.085, duration: 0.66 },
            0.16,
          )
          .from(
            ".download-heading, .platform-rail",
            { opacity: 0, y: 14, duration: 0.62, stagger: 0.07 },
            0.35,
          );

        gsap.from(".terrain", {
          y: 19,
          opacity: 0,
          duration: 1.35,
          ease: "power2.out",
        });
        gsap.to(".terrain-window", {
          yPercent: -13,
          ease: "none",
          scrollTrigger: {
            trigger: ".hero",
            start: "top top",
            end: "bottom top",
            scrub: 0.65,
          },
        });
        gsap.to(".sky-motion", {
          yPercent: 17,
          scale: 0.94,
          ease: "none",
          scrollTrigger: {
            trigger: ".hero",
            start: "top top",
            end: "bottom top",
            scrub: 0.6,
            invalidateOnRefresh: true,
          },
        });
        gsap.to(".star-sheet", {
          yPercent: -10,
          ease: "none",
          scrollTrigger: {
            trigger: ".hero",
            start: "top top",
            end: "bottom top",
            scrub: 0.85,
          },
        });
      });
      media.add(
        "(hover: hover) and (pointer: fine) and (prefers-reduced-motion: no-preference)",
        () => {
          const hero = scope.current.querySelector(".hero");
          const ridge = gsap.quickTo(
            hero.querySelector(".terrain-window"),
            "x",
            { duration: 0.85, ease: "power3.out" },
          );
          const sky = gsap.quickTo(hero.querySelector(".star-sheet"), "x", {
            duration: 1.05,
            ease: "power3.out",
          });
          function move(event) {
            if (hero.classList.contains("paused")) return;
            const bounds = hero.getBoundingClientRect();
            const fraction = (event.clientX - bounds.left) / bounds.width - 0.5;
            ridge(-fraction * 21);
            sky(fraction * 9);
          }
          function center() {
            ridge(0);
            sky(0);
          }
          hero.addEventListener("pointermove", move, { passive: true });
          hero.addEventListener("pointerleave", center);
          return () => {
            hero.removeEventListener("pointermove", move);
            hero.removeEventListener("pointerleave", center);
          };
        },
      );
      return () => media.revert();
    },
    { scope },
  );
}

export function usePackageMotion(container, selected) {
  useGSAP(
    () => {
      if (matchMedia("(prefers-reduced-motion: reduce)").matches) return;
      gsap.from(".package-row, .package-note", {
        opacity: 0.12,
        x: 8,
        y: 5,
        duration: 0.43,
        stagger: 0.055,
        clearProps: "transform,opacity",
        ease: "power3.out",
      });
    },
    { scope: container, dependencies: [selected], revertOnUpdate: true },
  );
}
