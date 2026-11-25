import { platforms, platformFor, unixDownload } from "./catalog.js";

const systems = Object.fromEntries(platforms.map((p) => [p.id, p]));
const universal = {
  linux: {
    id: "linux-universal",
    arch: "双架构",
    format: "TAR.GZ",
    version: "0.1.7",
    url: unixDownload("PiuLink-Client-0.1.7-linux-universal.tar.gz"),
  },
};
const platformNames = {
  Windows: "windows",
  macOS: "macos",
  Linux: "linux",
  Android: "android",
};

export function recommendDevice(userAgent, touch = 0, hints = {}) {
  const standIn = (name, detail) => ({ name, detail, file: null });
  if (
    /iPhone|iPod|iPad/i.test(userAgent) ||
    (/Macintosh/i.test(userAgent) && touch > 1) ||
    hints.platform === "iOS" ||
    hints.platform === "iPadOS"
  )
    return {
      id: "ios",
      name: "iOS / iPadOS",
      file: systems.ios.packages[0],
      detail: "未签名 IPA · 未经真机验证",
    };
  if (/CrOS/.test(userAgent) || hints.platform === "Chrome OS")
    return standIn("ChromeOS", "请选择目标平台");
  const id = platformNames[hints.platform] || platformFor(userAgent, touch);
  if (!id) return standIn("当前设备", "请选择目标平台");
  const { name, packages } = systems[id];
  const choice = (file) => ({
    id,
    name,
    file,
    detail:
      file.arch +
      " · " +
      file.format +
      (id === "macos" ? " · 未经 Mac 真机验证" : ""),
  });
  if (id === "android") return choice(packages[0]);
  const cpu = (hints.architecture || "").toLowerCase();
  const bits = hints.bitness;
  const arm64 =
    /aarch64|arm64/i.test(userAgent) ||
    (cpu === "arm" && (bits === "64" || id === "macos"));
  const x64 =
    /x86_64|amd64|x64|win64|wow64/i.test(userAgent) ||
    (cpu === "x86" && bits === "64") ||
    hints.wow64 === true;
  const is32 =
    (bits === "32" && !hints.wow64 && !arm64 && !x64) ||
    (/i[3-6]86|armv[5-8]l/i.test(userAgent) && !x64 && !arm64);
  if (is32 && id !== "macos") return standIn(name, "当前仅提供 64 位安装包");
  if (id === "windows") {
    if (arm64 || cpu === "arm")
      return standIn("Windows ARM", "当前提供 x64 安装包");
    // Windows' legacy UA can omit WOW64. Do not treat Win32 as a CPU claim.
    if (x64) return choice(packages[0]);
    return standIn(name, "请选择适用的安装包");
  }
  if (id === "macos") {
    if (arm64) return choice(packages[0]);
    // Safari on Apple Silicon may also advertise an Intel Mac. Only trust
    // explicit browser-provided architecture here, otherwise use both runtimes.
    if (cpu === "x86" && bits === "64") return choice(packages[1]);
    return choice(packages.find((item) => item.id === "mac-universal"));
  }
  if (/riscv|ppc|powerpc|s390|mips|loongarch/i.test(cpu + " " + userAgent))
    return standIn(name, "当前提供 x64 和 ARM64 安装包");
  if (arm64 || x64) {
    const file = packages[arm64 ? 1 : 0];
    // A generic Linux user agent does not identify the distribution's package manager.
    return choice(
      /Debian|Ubuntu|Linux Mint/i.test(userAgent)
        ? file
        : { ...file, ...file.portable },
    );
  }
  return choice(universal.linux);
}
