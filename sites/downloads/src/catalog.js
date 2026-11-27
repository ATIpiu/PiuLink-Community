export const repository = "https://github.com/ATIpiu/PiuLink-Community";
const appleTestNote =
  "博主的钱包还没解锁苹果全家桶，Mac、iPhone 和 iPad 都没做过真机实测。";
export const edition = {
  date: "2026-11-28",
  displayDate: "2026.11.28",
  channel: "Rust 全新主线 · 公开预览",
};

function published(tag, file) {
  return `${repository}/releases/download/${tag}/${file}`;
}
export function unixDownload(file) {
  return published("unix-harmony-updates-2026.11.27", file);
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
        description: "内含 Rust 主机引擎与控制端，可从同一台电脑发送或接收串流。",
        version: "0.2.141",
        arch: "x64",
        format: "EXE",
        bytes: 134219825,
        url: published("rust-2026.11.28", "PiuLink-Windows-0.2.141-win-x64.exe"),
      },
      {
        id: "win-client",
        title: "控制端",
        role: "client",
        description: "从这台电脑连接远端桌面。Rust 接收路径负责整帧重组与音视频丢包恢复。",
        version: "0.2.41",
        arch: "x64",
        format: "MSI",
        bytes: 89530860,
        url: published("rust-2026.11.28", "PiuLink-Client-0.2.41-win-x64.msi"),
      },
      {
        id: "win-host",
        title: "被控端",
        role: "host",
        description: "内置 Rust 参与的实时编码与媒体发送路径。",
        version: "0.2.138",
        arch: "x64",
        format: "MSI",
        bytes: 105287346,
        url: published("rust-2026.11.28", "PiuLink-Host-0.2.138-win-x64-preview.msi"),
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
        description: "包含串流底部快捷键栏，已在安卓平板实测。支持四种 CPU 架构。",
        version: "0.3.264",
        arch: "通用",
        format: "APK",
        bytes: 59798238,
        url: published("android-0.3.264", "PiuLink-Android-0.3.264-nonRoot-universal-release.apk"),
      },
      {
        id: "android-arm64",
        title: "ARM64 精简版",
        role: "mobile",
        description: "仅含 arm64-v8a，适合 64 位 ARM 设备。与通用版提供相同的串流快捷键。",
        version: "0.3.264",
        arch: "ARM64",
        format: "APK",
        bytes: 18082815,
        url: published("android-0.3.264", "PiuLink-Android-0.3.264-nonRoot-arm64-v8a-release.apk"),
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
        version: "0.1.12",
        arch: "Apple 芯片",
        format: "ZIP",
        bytes: 46930148,
        url: published("unix-harmony-updates-2026.11.27", "PiuLink-Mac-0.1.12-osx-arm64-unsigned.zip"),
      },
      {
        id: "mac-x64",
        title: "控制端",
        role: "client",
        description: "适用于搭载 Intel 处理器的 Mac。",
        version: "0.1.12",
        arch: "Intel x64",
        format: "ZIP",
        bytes: 49000041,
        url: published("unix-harmony-updates-2026.11.27", "PiuLink-Mac-0.1.12-osx-x64-unsigned.zip"),
      },
      {
        id: "mac-universal",
        title: "通用控制端",
        role: "client",
        description: "不确定 Mac 的芯片时，选这个双架构包。",
        version: "0.1.12",
        arch: "Apple 芯片 / Intel",
        format: "ZIP",
        bytes: 93851162,
        url: published("unix-harmony-updates-2026.11.27", "PiuLink-Mac-0.1.12-universal-unsigned.zip"),
      },
    ],
    testNote: "本轮没有 macOS Rust 安装包。下列下载来自旧主线。" + appleTestNote,
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
        version: "0.1.4",
        arch: "ARM64",
        format: "IPA",
        bytes: 7044329,
        url: published("mobile-preview-2026.11.27", "PiuLink-iOS-0.1.4-arm64-unsigned.ipa"),
      },
    ],
    testNote: "iPhone 模拟器已验证桌面画面、声音和键鼠输入。实体 iPhone 和 iPad 尚未实测。",
    note: "Rust 主线目前提供集成源码。下列旧版 IPA 由 Xcode 构建，未签名。也可以从源码构建并使用自己的开发团队签名。",
    source: repository + "/tree/mobile-preview-2026.11.27/apps/ios-client",
  },
  {
    id: "harmony",
    name: "鸿蒙",
    packages: [
      {
        id: "harmony-rust-simulator", title: "Rust 模拟器版", role: "mobile",
        description: "API 24 未签名开发包，已在模拟器验证。", version: "0.1.7",
        arch: "ARM64 / x86_64", format: "HAP", bytes: 39255396,
        url: published("rust-2026.11.28", "PiuLink-Harmony-0.1.7-API24-emulator-unsigned.hap"),
      },
      {
        id: "harmony-client",
        title: "旧版原生控制端",
        role: "mobile",
        description: "在同一局域网内添加电脑，使用 PIN 配对后即可打开桌面。",
        version: "0.1.5",
        arch: "ARM64 / x86_64",
        format: "HAP",
        bytes: 36636788,
        url: published("harmony-0.1.5", "PiuLink-HarmonyOS-0.1.5-unsigned.hap"),
      },
    ],
    testNote: "DevEco Studio 手机模拟器已验证桌面画面、声音和输入。实体设备尚未实测。",
    note: "可在「设置 → 关于 → 检查更新」增量下载 HAP。模拟器可安装此开发包，真机需要与设备匹配的签名及调试授权。",
    source: repository + "/tree/main/apps/harmony-client",
  },
  {
    id: "linux",
    name: "Linux",
    packages: [
      {
        id: "linux-suite",
        title: "整合版",
        role: "linux",
        description: "Ubuntu 26.04 x64。控制端内置 Rust 接收引擎，被控端首次设置需联网。",
        version: "0.1.1",
        arch: "Ubuntu 26.04 x64",
        format: "DEB",
        bytes: 42049592,
        url: published("rust-2026.11.28", "piulink-suite_0.1.1-1_amd64.deb"),
      },
      {
        id: "linux-x64",
        title: "控制端",
        role: "client",
        description: "适用于 Intel 或 AMD 64 位桌面设备。",
        version: "0.1.12",
        arch: "x64",
        format: "DEB",
        bytes: 36764216,
        url: published("unix-harmony-updates-2026.11.27", "piulink-client_0.1.12-1_amd64.deb"),
        portable: {
          format: "TAR.GZ",
          bytes: 44535978,
          url: published("unix-harmony-updates-2026.11.27", "PiuLink-Client-0.1.12-linux-x64.tar.gz"),
        },
      },
      {
        id: "linux-arm64",
        title: "控制端",
        role: "client",
        description: "适用于 ARM 64 位桌面设备。",
        version: "0.1.12",
        arch: "ARM64",
        format: "DEB",
        bytes: 33409432,
        url: published("unix-harmony-updates-2026.11.27", "piulink-client_0.1.12-1_arm64.deb"),
        portable: {
          format: "TAR.GZ",
          bytes: 42137107,
          url: published("unix-harmony-updates-2026.11.27", "PiuLink-Client-0.1.12-linux-arm64.tar.gz"),
        },
      },
      {
        id: "linux-host",
        title: "被控端",
        role: "host",
        description: "适用于 Ubuntu 26.04 x64 电脑。",
        version: "0.1.9",
        arch: "x64",
        format: "DEB",
        bytes: 3608784,
        url: published("unix-harmony-updates-2026.11.27", "piulink-host_0.1.9-1_amd64.deb"),
        portable: {
          format: "TAR.GZ",
          bytes: 4244653,
          url: published("unix-harmony-updates-2026.11.27", "PiuLink-Host-0.1.9-ubuntu26.04-x64.tar.gz"),
        },
      },
    ],
    note: "Ubuntu 26.04 x64 可用 apt 安装整合版，控制端可直接从应用菜单启动。独立控制端需另装 Moonlight Qt。Ubuntu 26.04 x64 被控端安装后，请用桌面用户运行 piulink-host-setup-user 配置运行环境。",
    source: repository + "/tree/main/apps/linux-suite",
  },
];

export function platformFor(userAgent, touchPoints = 0) {
  if (/OpenHarmony|HarmonyOS NEXT/i.test(userAgent)) return "harmony";
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
