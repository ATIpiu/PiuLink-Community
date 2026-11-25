import React from "react";
import {
  faAndroid,
  faApple,
  faGithub,
  faLinux,
} from "@fortawesome/free-brands-svg-icons";

const brands = {
  android: faAndroid,
  macos: faApple,
  ios: faApple,
  github: faGithub,
  linux: faLinux,
};
const strokes = {
  download: (
    <>
      <path d="M12 3v11m-4-4 4 4 4-4" />
      <path d="M5 15v4a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-4" />
    </>
  ),
  external: <path d="M6 18 18 6M7 6h11v11" />,
  down: <path d="M5 12h14m-7-7 7 7-7 7" />,
  client: (
    <>
      <rect x="3" y="4" width="18" height="13" rx="2" />
      <path d="M8 21h8m-4-4v4" />
    </>
  ),
  host: (
    <>
      <rect x="6" y="2.5" width="12" height="19" rx="2" />
      <path d="M9.5 7h5m-5 3h5m-3 7h1" />
    </>
  ),
  mobile: (
    <>
      <rect x="6.5" y="2" width="11" height="20" rx="2" />
      <path d="M10.5 18.5h3" />
    </>
  ),
};

export function Icon({ name, size = 20 }) {
  const brand = brands[name];
  if (brand) {
    const [width, height, , , path] = brand.icon;
    return (
      <svg
        aria-hidden="true"
        width={size}
        height={size}
        viewBox={`0 0 ${width} ${height}`}
        fill="currentColor"
      >
        <path d={path} />
      </svg>
    );
  }
  if (name === "windows")
    return (
      <svg
        aria-hidden="true"
        width={size}
        height={size}
        viewBox="0 0 24 24"
        fill="currentColor"
      >
        <path d="M2 2h9.4v9.4H2zM12.6 2H22v9.4h-9.4zM2 12.6h9.4V22H2zM12.6 12.6H22V22h-9.4z" />
      </svg>
    );
  return (
    <svg
      aria-hidden="true"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.65"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      {strokes[name]}
    </svg>
  );
}
