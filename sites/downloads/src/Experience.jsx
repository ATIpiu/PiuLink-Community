import React, { useEffect, useLayoutEffect, useRef, useState } from "react";
import "@fontsource-variable/geist";
import "@fontsource/instrument-serif/latin-400-italic.css";
import { edition, platformFor, platforms, repository } from "./catalog.js";
import { Icon } from "./Icons.jsx";
import { LunarStage } from "./LunarStage.jsx";
import { recommendDevice } from "./device.js";
import { usePackageMotion, useSceneMotion } from "./sceneMotion.js";
import "./motion.css";

const system = platformFor(navigator.userAgent, navigator.maxTouchPoints);
const requested = new URLSearchParams(location.search).get("platform");
const initial =
  platforms.find((p) => p.id === requested)?.id ?? system ?? "windows";
const base = import.meta.env.BASE_URL;

function Brand() {
  return (
    <span className="brand">
      <img src={base + "piulink.svg"} alt="" />
      PiuLink
    </span>
  );
}

function Stars() {
  // Deterministic coordinates keep the sky stable through tab changes.
  let state = 19317;
  const random = () => {
    state = (state * 16807) % 2147483647;
    return state / 2147483647;
  };
  return (
    <svg
      className="star-sheet"
      viewBox="0 0 1440 550"
      preserveAspectRatio="xMidYMid slice"
      aria-hidden="true"
    >
      {Array.from({ length: 117 }, (_, i) => (
        <circle
          key={i}
          cx={random() * 1440}
          cy={random() * 550}
          r={0.38 + random() * 0.82}
          opacity={0.14 + random() * 0.55}
          className={i % 15 === 0 ? "spark" : undefined}
        />
      ))}
    </svg>
  );
}
const stars = <Stars />;

function Hero({ selected, setSystem, playFilm }) {
  const [device, setDevice] = useState(() =>
    recommendDevice(
      navigator.userAgent,
      navigator.maxTouchPoints,
      navigator.userAgentData || {},
    ),
  );
  useEffect(() => {
    let mounted = true;
    navigator.userAgentData
      ?.getHighEntropyValues?.(["architecture", "bitness", "wow64"])
      .then((hints) => {
        if (mounted)
          setDevice(
            recommendDevice(
              navigator.userAgent,
              navigator.maxTouchPoints,
              hints,
            ),
          );
      })
      .catch(() => {});
    return () => {
      mounted = false;
    };
  }, []);
  const [paused, setPaused] = useState(false);
  const downloadLabel = device.file?.id === "linux-suite" ? "下载 Linux 整合版"
    : device.id === "windows" ? "下载 Windows 整合版"
    : device.id === "android" ? "下载 Android 通用版" : device.id === "ios" ? "下载未签名 IPA" : `下载 ${device.name} 版`;

  const [reduced, setReduced] = useState(
    () => matchMedia("(prefers-reduced-motion: reduce)").matches,
  );
  useEffect(() => {
    const query = matchMedia("(prefers-reduced-motion: reduce)");
    const update = () => setReduced(query.matches);
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);
  return (
    <section
      className={"hero" + (paused ? " paused" : "")}
      aria-labelledby="title"
    >
      <div className="hero-atmosphere" />
      {stars}
      <div className="wrap hero-layout">
        <div className="sky-motion">
          <LunarStage selected={selected} paused={paused} />
        </div>
        <div className="terrain-window" aria-hidden="true">
          <img className="terrain" src={base + "lunar-horizon.webp"} alt="" />
        </div>
        <div className="hero-copy">
          <h1 id="title" aria-label="PiuLink 下载中心">
            <span className="letter-mask" aria-hidden="true">
              <span className="title-word">Piu</span>
              <em className="title-word">Link</em>
            </span>
          </h1>
          <p className="hero-description">用手边的设备，连接远处的电脑。</p>
          <div className="hero-actions">
            {device.file ? (
              <a
                className="primary-button"
                href={device.file.url}
                download
                aria-label={`${downloadLabel}，${device.detail}`}
              >
                <span>
                  {downloadLabel}
                </span>
                <span className="button-arrow">
                  <Icon name="download" size={16} />
                </span>
              </a>
            ) : (
              <a
                className="primary-button"
                href="#downloads"
                onClick={() => system && setSystem(system)}
              >
                <span>查看可用安装包</span>
                <span className="button-arrow">
                  <Icon name="down" size={16} />
                </span>
              </a>
            )}
            <span className="device-match" aria-live="polite">
              <strong>{device.file ? `v${device.file.version}` : device.name}</strong>
              <small>{device.detail}{device.file?.bytes && ` · ${(device.file.bytes / 1048576).toFixed(1)} MiB`}</small>
            </span>
          </div>
          <a className="watch-link" href="#film" onClick={playFilm}>
            <span className="watch-icon" aria-hidden="true">▶</span>
            观看产品短片 <time dateTime="PT1M3S">1:03</time>
          </a>
        </div>
        {!reduced && (
          <button
            className="motion-switch"
            type="button"
            aria-label={paused ? "播放月球动画" : "暂停月球动画"}
            title={paused ? "播放月球动画" : "暂停月球动画"}
            onClick={() => setPaused((v) => !v)}
          >
            <svg
              viewBox="0 0 20 20"
              width="13"
              height="13"
              fill="currentColor"
              aria-hidden="true"
            >
              {paused ? (
                <path d="m6 3 10 7-10 7z" />
              ) : (
                <path d="M5 4h3v12H5zm7 0h3v12h-3z" />
              )}
            </svg>
          </button>
        )}
      </div>
    </section>
  );
}

function ProductFilm({ player }) {
  return (
    <section id="film" className="wrap film-section" aria-labelledby="film-title">
      <div className="film-copy">
        <h2 id="film-title">PiuLink 产品短片</h2>
        <p>在客厅用电视接着玩，出门用手机回到桌面。看同一台电脑如何在几块屏幕之间切换。</p>
        <span className="film-meta">1 分 03 秒 · 中文旁白与字幕</span>
      </div>
      <video
        ref={player}
        className="film-player"
        aria-labelledby="film-title"
        controls
        playsInline
        preload="none"
        width="1920"
        height="1080"
        poster={base + "media/film-poster.jpg"}
      >
        <source src={base + "media/piulink-film.mp4"} type="video/mp4" />
        <track kind="captions" srcLang="zh-CN" label="简体中文" src={base + "media/piulink-zh.vtt"} />
        <a href={base + "media/piulink-film.mp4"}>打开 MP4 视频</a>
      </video>
    </section>
  );
}

function PackageRow({ item, platform }) {
  const size =
    item.bytes >= 1048576
      ? (item.bytes / 1048576).toFixed(1) + " MiB"
      : Math.round(item.bytes / 1024) + " KiB";
  return (
    <li className={"package-row" + (["win-suite", "android-client", "linux-suite"].includes(item.id) ? " recommended" : "")}>
      <span className="package-icon" aria-hidden="true"><Icon name={item.role} size={22} /></span>
      <div className="package-info">
        <div className="package-title">
          <h3>{item.title}</h3>
          <span className="arch">{item.arch}</span>
        </div>
        <p>{item.description}</p>
        <small className="package-meta">v{item.version} · {size}</small>
      </div>
      <div className="package-actions">
        <a
          href={item.url}
          download
          className="file-link"
          aria-label={`下载 ${platform} ${item.title} ${item.arch} ${item.format}`}
        >
          <span className="file-arrow">
            <Icon name="download" size={15} />
          </span>
          <span>下载 {item.format}</span>
        </a>
        {item.portable && (
          <a
            className="portable-link"
            href={item.portable.url}
            download
            aria-label={`下载 ${platform} ${item.title} ${item.arch} 便携压缩包`}
          >
            便携压缩包 · {item.portable.format}
          </a>
        )}
      </div>
    </li>
  );
}

function Downloads({ selected, setSystem }) {
  const content = useRef(null);
  usePackageMotion(content, selected);
  useLayoutEffect(() => {
    const deck = content.current;
    const panel = deck.querySelector('[role="tabpanel"]:not([hidden])');
    const resize = () => {
      deck.style.height =
        Math.ceil(panel.getBoundingClientRect().height) + "px";
    };
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(panel);
    return () => observer.disconnect();
  }, [selected]);
  function move(event, index) {
    const count = platforms.length;
    const next = {
      ArrowRight: (index + 1) % count,
      ArrowLeft: (index + count - 1) % count,
      Home: 0,
      End: count - 1,
    }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    const id = platforms[next].id;
    setSystem(id);
    document.getElementById("tab-" + id).focus();
  }
  return (
    <section
      id="downloads"
      className="wrap downloads"
      aria-labelledby="downloads-title"
    >
      <div className="download-heading">
        <h2 id="downloads-title">按平台下载</h2>
        <div className="release">
          {edition.channel}
          <span> / </span>
          <time dateTime={edition.date}>{edition.displayDate}</time>
        </div>
        <a href="#changelog">
          更新记录 <Icon name="down" size={13} />
        </a>
      </div>
      <div className="platform-picker">
        <div
          className="platform-rail"
          role="tablist"
          aria-label="下载平台"
        >
          
          {platforms.map((p, i) => (
            <button
              key={p.id}
              id={"tab-" + p.id}
              role="tab"
              aria-selected={selected === p.id}
              aria-controls={"panel-" + p.id}
              tabIndex={selected === p.id ? 0 : -1}
              onClick={() => setSystem(p.id)}
              onKeyDown={(e) => move(e, i)}
            >
              <Icon name={p.id} size={17} />
              {p.name}
            </button>
          ))}
        </div>
        <div ref={content} className="package-deck">
          {platforms.map((p) => (
            <div
              key={p.id}
              id={"panel-" + p.id}
              role="tabpanel"
              aria-labelledby={"tab-" + p.id}
              hidden={selected !== p.id}
            >
              {selected === p.id && (
                <>
                  {p.testNote && (
                    <p className="apple-preview-note">{p.testNote}</p>
                  )}
                  {p.packages.length > 0 && (
                    <ul>
                      {p.packages.map((item) => (
                        <PackageRow
                          key={item.id}
                          item={item}
                          platform={p.name}
                        />
                      ))}
                    </ul>
                  )}
                  {p.note && (
                    <p className="package-note">
                      {p.note}
                      {p.source && (
                        <>
                          {" "}
                          <a href={p.source}>源码与构建说明</a>
                        </>
                      )}
                    </p>
                  )}
                </>
              )}
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}

const updates = [
  {
    title: "Windows 整合版 0.2.142",
    tag: "windows-0.2.142",
    date: "2026-11-28",
    changes: [
      "Windows x64 整合版包含 Host 0.2.140 和 Client 0.2.41。主机管理页、引擎图标与托盘使用 PiuLink 标识。",
      "已安装公开整合版 0.2.141 的用户可在应用内检查更新。使用四段版本号的旧开发版请从官网下载整合包。",
    ],
  },
  {
    title: "Android 0.3.264 · 串流底部快捷键栏",
    tag: "android-0.3.264",
    date: "2026-11-28",
    changes: [
      "在串流画面底部轻点即可复制、粘贴、切换窗口或发送 Esc。打开软键盘后也能使用快捷键。",
      "快捷键栏可设为自动、始终显示或关闭。自动模式会在连接实体键盘时隐藏，断开后恢复。",
      "通用版与 ARM64 精简版均可在应用设置中检查更新。",
    ],
  },
  {
  "title": "PiuLink Rust · 全新主线",
  "tag": "rust-2026.11.28",
  "date": "2026-11-28",
  "changes": [
    "Windows Host 与控制端采用 Rust 串流代码。桌面控制端与 Android 共用视频整帧重组及音视频丢包恢复逻辑。",
    "Ubuntu 26.04 x64 整合 DEB 内置 Rust 接收引擎，可从应用菜单直接开始串流。",
    "PiuLink Rust 是完全全新的开发版本。自本次发布起，后续优化、修复和功能迭代统一基于新的 main。旧版 fork、旧版 commit 和基于旧主线的 PR 均不接受合并。希望继续贡献的开发者，请从新的 main 创建新的 fork 或分支，重新实现所需改动并提交新的 PR。",
    "鸿蒙提供 API 24 模拟器开发包；iOS 提供已接入 Rust 的源码，本次未发布 iOS IPA 或 macOS 安装包。"
  ]
},
  {
    title: "Linux 整合版 0.1.0",
    tag: "linux-suite-0.1.0",
    date: "2026-11-27",
    changes: [
      "Ubuntu 26.04 x64 可以只安装一个 DEB，同时获得控制端和被控端。首次打开组件设置时会校验下载的 Moonlight 和 Sunshine。",
      "整合版有独立的增量更新通道。升级时会显示实际下载量，已安装的单独版可通过 apt 切换到整合版。",
    ],
  },
  {
    title: "鸿蒙、Linux 和 macOS 增量更新",
    tag: "unix-harmony-updates-2026.11.27",
    date: "2026-11-27",
    changes: [
      "鸿蒙 0.1.5 可在「设置 → 关于 → 检查更新」下载并保存 HAP。手机模拟器从 0.1.4 升级时下载 6.60 MiB，复用 28.34 MiB。0.1.2 及更早版本需先手动安装一次。",
      "Linux 控制端 0.1.12 的 x64 和 ARM64 DEB 从 0.1.11 升级时分别下载 10.17 和 9.06 MiB。WSL2 被控端从 0.1.8 升至 0.1.9，下载 92,282 字节。",
      "macOS 控制端 0.1.12 的 Apple 芯片、Intel 和通用安装包都通过了 GitHub 分段下载与完整文件校验。macOS 和 iOS 未做苹果真机验证。",
    ],
  },
  {
    title: "iOS 0.1.4 和鸿蒙原生 0.1.2",
    tag: "mobile-preview-2026.11.27",
    date: "2026-11-27",
    changes: [
      "iOS 保留应用内更新，修复了带封面的应用卡片名称；鸿蒙调整了 x86 手机模拟器的 4K 自动码率上限。",
      "两个客户端均已在手机模拟器上完成桌面串流、音频输出和键鼠输入验证。下载页提供未签名 IPA 与 HAP。",
    ],
  },
  {
    title: "Windows 整合版 0.2.140 · 升级后自动恢复 Host",
    tag: "windows-0.2.140",
    date: "2026-11-27",
    changes: [
      "Windows 默认下载升级为 0.2.140 整合版，内含被控端 0.2.138、控制端 0.2.39。安装器会在升级后恢复之前运行的 Host。",
      "在已安装的 0.2.139 中点击「检查更新」，实际下载安装包内容 20.26 MiB，复用旧包 99.39 MiB，完整包 119.65 MiB。",
      "实机升级保留 14 个配对。Host 自动恢复到托盘后，安卓平板首次点击原电脑卡片即进入桌面，3.535 秒出首帧。连续三次连接均无需重新配对。",
    ],
  },
  {
    title: "Windows 0.2.139 · 应用内升级实测",
    tag: "windows-0.2.139",
    date: "2026-11-27",
    changes: [
      "从 0.2.138 点击「检查更新」，有可复用旧包时从 GitHub 下载 19.93 MiB，复用 99.62 MiB。旧包不在该版本可识别的位置时，实测下载完整的 119.54 MiB。",
      "0.2.139 开始会自动查找 Windows 记录的原安装包位置。本次升级保留了 14 个配对。安装器关闭 Host 后未自动重启，平板首次回连失败。在开始菜单重新打开 PiuLink Host 后，从原电脑卡片连续三次进入桌面，均无需重新配对。",
      "0.2.139 发布时供软件内升级验证，当时的官网默认下载为 0.2.138。",
    ],
  },
  {
    title: "Windows 默认下载升级至 0.2.138",
    tag: "desktop-updates-2026.11.27",
    date: "2026-11-27",
    changes: [
      "Windows x64 默认下载现为 0.2.138 整合版，包含被控端 0.2.136 和控制端 0.2.37，可在软件内检查更新。从 0.2.136 覆盖升级已在实机验证，配置和 14 个已有配对全部保留。",
      "安卓平板使用原配对连续三次打开桌面，点击后均在 1.05 秒内出画面。每次断开后，主机均恢复原双屏布局。",
      "Windows 整合预览版 0.2.138 把运行库与应用分开压缩，Linux 客户端 0.1.11 和被控端 0.1.8 的 DEB 按文件分块。旧布局首次升级可能接近完整下载。",
      "以两个真实版本的内容采用新布局进行本地对照：Windows 下载 38.25 / 119.45 MiB，Linux x64 DEB 下载 13.44 / 35.06 MiB。Mac 和 Linux 客户端也可以选择其他文件夹中的旧安装包。",
    ],
  },
  {
    title: "跨平台增量更新预览",
    tag: "updates-preview-2026.11.27",
    date: "2026-11-27",
    changes: [
      "Windows 整合版 0.2.137、macOS / Linux 控制端 0.1.10、Linux 被控端 0.1.7 和 iOS 0.1.3 加入检查更新。更新文件来自 GitHub，复用旧包中相同的内容并验证完整文件。",
      "macOS 和 iOS 未做真机验证，iOS 更新后仍需自行签名。",
    ],
  },
  {
    title: "Android 0.3.225",
    tag: "android-0.3.225",
    date: "2026-11-27",
    changes: [
      "在「设置 → 高级选项 → 检查更新」复用当前 APK 下载新版本，由系统确认安装。旧版需从 GitHub 覆盖安装一次。平板从 0.3.224 升至 0.3.225 时，55.43 MiB 的通用包实际下载 2.50 MiB。",
      "在「设置 → 界面外观」选择哪些提醒可以弹出，或恢复以前勾选了「下次不再提醒」的提示。",
    ],
  },
  {
    title: "Windows 整合版 0.2.136",
    tag: "windows-0.2.136",
    date: "2026-11-27",
    changes: [
      "Windows 默认下载的离线整合版从 180.42 MiB 减至 118.47 MiB。内含 Host 0.2.135 和 Client 0.2.36，两端共用的运行库只下载一次。",
      "已在 Windows x64 实测从 0.2.135 升级、从系统维护入口修复，以及卸载后重装。14 个已有配对、Host 配置、客户端连接历史和证书均保留。",
    ],
  },
  {
    title: "Android 0.3.222",
    tag: "android-0.3.222",
    date: "2026-11-27",
    changes: [
      "电脑列表会同时检查当前地址、上次连接线路和历史记录里属于当前局域网的地址。换网后，一个失效地址不会挡住已经响应的电脑。",
      "已配对电脑在刷新期间可以直接点「桌面」。连接前仍会校验主机身份，局域网广播发现新电脑后会立即尝试连接。",
    ],
  },
  {
    title: "Windows 整合版 0.2.135",
    tag: "windows-0.2.135",
    date: "2026-11-27",
    changes: [
      "Windows 默认下载整合版，一次安装控制端 0.2.36 和被控端 0.2.135。只需要一个角色时，可在 Windows 下载列表选择单独的 MSI。",
      "被控端离线安装包从上一公开版的 148.9 MiB 减至 95.1 MiB，保留串流引擎、P2P、网页访问和 Windows 分享。超级屏断开后可恢复实体屏布局。",
    ],
  },
  {
    title: "Android 0.3.209",
    tag: "android-0.3.209",
    date: "2026-11-26",
    changes: [
      "丢帧时默认保留最后一张完整画面，恢复期间保持编码码率。持续弱网时可能短暂停画。",
      "使用 Clash 时，可在「设置 → 画面与网络」开启「P2P 串流优先直连」。VPN 允许绕行时，原邀请可以通过 Wi-Fi 或移动数据串流，性能面板会显示实际选路。",
      "串流内的「屏幕设置」把分辨率和帧率放在「画面」，把切屏和开关放在「显示器」。电脑卡片可以直接打开已保存的邀请，或进入「超级屏」。",
    ],
  },
  {
    title: "Windows Host 0.2.131",
    tag: "host-0.2.131",
    date: "2026-11-26",
    changes: [
      "Android 0.3.206 连接这台电脑后，可在串流菜单打开「屏幕」，直接调整串流帧率和分辨率。画面在当前会话中切换，无需断开重连。",
      "已用安卓平板实测普通桌面和「超级屏」在 4K 与 1080p 之间往返切换，帧率可在 30 与 60 FPS 之间切换。GameViewer 虚拟显示器占用桌面时会阻止「超级屏」启动。",
    ],
  },
  {
    title: "Android 0.3.206",
    tag: "android-0.3.206",
    date: "2026-11-26",
    changes: [
      "同一台电脑的内网 IP、外网 IP 和邀请链接保存在一起。切换网络后，点电脑即可自动选路，连接失败时可以手动选。",
      "开启 VPN 时，如果同一 Wi-Fi 上的电脑可直连，客户端会优先走局域网。经 VPN 串流时，自动码率会根据持续升高的往返延迟提前下调。",
      "连接支持实时调整的 Host 后，可在串流菜单的「屏幕」面板切换帧率和分辨率，无需重新连接。",
    ],
  },
  {
    title: "Android 0.3.186",
    tag: "android-0.3.186",
    date: "2026-11-25",
    changes: ["触控板新增七档鼠标速度。在串流菜单里调节后立即生效，下次连接也会记住。"],
  },
];

function ReleaseNote({ entry }) {
  return (
    <details className="release-entry">
      <summary>
        <time dateTime={entry.date}>{entry.date.replaceAll("-", ".")}</time>
        <span>{entry.title}</span>
        <i aria-hidden="true" />
      </summary>
      <div className="release-body">
        <ul>{entry.changes.map((change) => <li key={change}>{change}</li>)}</ul>
        <a href={repository + "/releases/tag/" + entry.tag}>在 GitHub 查看此版本 <Icon name="external" size={13} /></a>
      </div>
    </details>
  );
}

function Changelog() {
  return (
    <section id="changelog" className="wrap changelog" aria-labelledby="changelog-title">
      <div className="changelog-heading">
        <h2 id="changelog-title">更新记录</h2>
        <a href={repository + "/releases"}>GitHub Releases <Icon name="external" size={14} /></a>
      </div>
      <div className="changelog-list">
        {updates.slice(0, 3).map((entry) => <ReleaseNote key={entry.tag} entry={entry} />)}
        <details className="older-notes">
          <summary>更早的版本（{updates.length - 3}）</summary>
          {updates.slice(3).map((entry) => <ReleaseNote key={entry.tag} entry={entry} />)}
        </details>
      </div>
    </section>
  );
}

export function Experience() {
  const [selected, setSelected] = useState(initial);
  const page = useRef(null);
  const player = useRef(null);
  useSceneMotion(page);
  useEffect(() => {
    if (["#film", "#downloads", "#changelog"].includes(location.hash)) {
      document.getElementById(location.hash.slice(1)).scrollIntoView({ behavior: "instant" });
    }
  }, []);
  function playFilm(event) {
    if (!event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
      player.current.play().catch(() => {
        player.current?.focus({ preventScroll: true });
      });
    }
  }
  function setSystem(id) {
    setSelected(id);
    const url = new URL(location.href);
    url.searchParams.set("platform", id);
    history.replaceState(null, "", url);
  }
  return (
    <div ref={page} className="site">
      <a className="skip" href="#downloads">
        跳到安装包
      </a>
      <header className="masthead">
        <div className="wrap nav">
          <a href="./" aria-label="PiuLink 首页">
            <Brand />
          </a>
          <nav aria-label="主导航">
            <a className="current-link" href="#downloads">
              所有平台
            </a>
            <a className="repo-link" href={repository}>
              <Icon name="github" size={16} />
              GitHub <Icon name="external" size={12} />
            </a>
          </nav>
        </div>
      </header>
      <main>
        <Hero selected={selected} setSystem={setSystem} playFilm={playFilm} />
        <Downloads selected={selected} setSystem={setSystem} />
        <ProductFilm player={player} />
        <Changelog />
      </main>
      <footer className="wrap footer">
        <span>© {edition.date.slice(0, 4)} ATIPiu Labs</span>
        <a href="https://svs.gsfc.nasa.gov/4720/">月面数据 · NASA SVS</a>
      </footer>
    </div>
  );
}
