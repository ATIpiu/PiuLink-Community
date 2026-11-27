export const repository = "https://github.com/ATIpiu/PiuLink-Community";
const appleTestNote =
  "博主的钱包还没解锁苹果全家桶，Mac、iPhone 和 iPad 都没做过真机实测。";
export const edition = {
  date: "2026-11-27",
  displayDate: "2026.11.27",
  channel: "公开预览版",
};

function published(tag, file) {
  return `${repository}/releases/download/${tag}/${file}`;
}
export function unixDownload(file) {
  return published("desktop-updates-2026.11.27", file);
}

export const platforms = [
  {
    id: "windows",
    name: "Windows",
    packages: [
      {
        id: "win-suite",
        title: "整合版（推荐）",
        role: "windows",
        description: "一次离线安装控制端和被控端，支持应用内增量更新。",
        version: "0.2.140",
        arch: "x64",
        format: "EXE",
        bytes: 125464733,
        url: published("windows-0.2.140", "PiuLink-Windows-0.2.140-win-x64.exe"),
      },
      {
        id: "win-client",
        title: "控制端",
        role: "client",
        description: "用这台电脑连接远端桌面。",
        version: "0.2.37",
        arch: "x64",
        format: "MSI",
        bytes: 89444908,
        url: published("updates-preview-2026.11.27", "PiuLink-Client-0.2.37-win-x64.msi"),
      },
      {
        id: "win-host",
        title: "被控端",
        role: "host",
        description: "让其他设备连到这台电脑。",
        version: "0.2.136",
        arch: "x64",
        format: "MSI",
        bytes: 99835851,
        url: published("updates-preview-2026.11.27", "PiuLink-Host-0.2.136-win-x64-preview.msi"),
      },
    ],
  },
  {
    id: "android",
    name: "Android",
    packages: [
      {
        id: "android-client",
        title: "通用版（推荐）",
        role: "mobile",
        description: "手机、平板和电视都可选。不确定设备架构就下载这一版。",
        version: "0.3.225",
        arch: "通用",
        format: "APK",
        bytes: 58122234,
        url: published("android-0.3.225", "PiuLink-Android-0.3.225-nonRoot-universal-release.apk"),
      },
      {
        id: "android-arm64",
        title: "ARM64 专用版",
        role: "mobile",
        description: "适合确认系统支持 arm64-v8a 的设备。功能与通用版相同。",
        version: "0.3.225",
        arch: "ARM64",
        format: "APK",
        bytes: 17667563,
        url: published("android-0.3.225", "PiuLink-Android-0.3.225-nonRoot-arm64-v8a-release.apk"),
      },
    ],
  },
  {
    id: "macos",
    name: "macOS",
    packages: [
      {
        id: "mac-arm64",
        title: "控制端",
        role: "client",
        description: "适用于搭载 Apple M 系列芯片的 Mac。",
        version: "0.1.11",
        arch: "Apple 芯片",
        format: "ZIP",
        bytes: 46929688,
        url: published("desktop-updates-2026.11.27", "PiuLink-Mac-0.1.11-osx-arm64-unsigned.zip"),
      },
      {
        id: "mac-x64",
        title: "控制端",
        role: "client",
        description: "适用于搭载 Intel 处理器的 Mac。",
        version: "0.1.11",
        arch: "Intel x64",
        format: "ZIP",
        bytes: 48999582,
        url: published("desktop-updates-2026.11.27", "PiuLink-Mac-0.1.11-osx-x64-unsigned.zip"),
      },
      {
        id: "mac-universal",
        title: "通用控制端",
        role: "client",
        description: "不确定 Mac 的芯片时，选这个双架构包。",
        version: "0.1.11",
        arch: "Apple 芯片 / Intel",
        format: "ZIP",
        bytes: 93850255,
        url: published("desktop-updates-2026.11.27", "PiuLink-Mac-0.1.11-universal-unsigned.zip"),
      },
    ],
    testNote: appleTestNote,
    note: "需要 macOS 12 或更高版本及 Moonlight.app。预览包未经开发者签名或公证。",
  },
  {
    id: "ios",
    name: "iOS",
    packages: [
      {
        id: "ios-client",
        title: "iPhone / iPad 控制端",
        role: "mobile",
        description:
          "适用于 iOS / iPadOS 15 及以上。下载后须自行签名才能安装。",
        version: "0.1.3",
        arch: "ARM64",
        format: "IPA",
        bytes: 7042434,
        url: published("updates-preview-2026.11.27", "PiuLink-iOS-0.1.3-arm64-unsigned.ipa"),
      },
    ],
    testNote: appleTestNote,
    note: "此 IPA 由 Xcode 构建，未签名。也可以从源码构建并使用自己的开发团队签名。",
    source: repository + "/tree/7cc2883/apps/ios-client",
  },
  {
    id: "linux",
    name: "Linux",
    packages: [
      {
        id: "linux-x64",
        title: "控制端",
        role: "client",
        description: "适用于 Intel 或 AMD 64 位桌面设备。",
        version: "0.1.11",
        arch: "x64",
        format: "DEB",
        bytes: 36763960,
        url: published("desktop-updates-2026.11.27", "piulink-client_0.1.11-1_amd64.deb"),
        portable: {
          format: "TAR.GZ",
          bytes: 44536069,
          url: published("desktop-updates-2026.11.27", "PiuLink-Client-0.1.11-linux-x64.tar.gz"),
        },
      },
      {
        id: "linux-arm64",
        title: "控制端",
        role: "client",
        description: "适用于 ARM 64 位桌面设备。",
        version: "0.1.11",
        arch: "ARM64",
        format: "DEB",
        bytes: 33409172,
        url: published("desktop-updates-2026.11.27", "piulink-client_0.1.11-1_arm64.deb"),
        portable: {
          format: "TAR.GZ",
          bytes: 42137128,
          url: published("desktop-updates-2026.11.27", "PiuLink-Client-0.1.11-linux-arm64.tar.gz"),
        },
      },
      {
        id: "linux-host",
        title: "被控端",
        role: "host",
        description: "适用于 Ubuntu 26.04 x64 电脑。",
        version: "0.1.8",
        arch: "x64",
        format: "DEB",
        bytes: 3608460,
        url: published("desktop-updates-2026.11.27", "piulink-host_0.1.8-1_amd64.deb"),
        portable: {
          format: "TAR.GZ",
          bytes: 4244210,
          url: published("desktop-updates-2026.11.27", "PiuLink-Host-0.1.8-ubuntu26.04-x64.tar.gz"),
        },
      },
    ],
    note: "Debian / Ubuntu 可用 apt 安装 DEB。控制端需另装 Moonlight Qt。Ubuntu 26.04 x64 被控端安装后，请用桌面用户运行 piulink-host-setup-user 配置运行环境。",
  },
];

export function platformFor(userAgent, touchPoints = 0) {
  if (/Android/i.test(userAgent)) return "android";
  if (
    /iPhone|iPad|iPod/i.test(userAgent) ||
    (/Macintosh/i.test(userAgent) && touchPoints > 1)
  )
    return "ios";
  if (/Windows NT/i.test(userAgent)) return "windows";
  if (/Macintosh|Mac OS X/i.test(userAgent)) return "macos";
  if (/Linux/i.test(userAgent) && !/CrOS/i.test(userAgent)) return "linux";
  return null;
}
