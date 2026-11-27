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
  const downloadLabel = device.id === "windows" ? "下载 Windows 整合版"
    : device.id === "ios" ? "下载未签名 IPA" : "下载当前设备版";
  const roleLabel = device.id === "windows" ? "整合版" : "控制端";
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
              <strong>
                {device.name}
                {device.file && ` ${roleLabel}`}
              </strong>
              <small>{device.detail}</small>
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
    <li className="package-row">
      <div className="package-info">
        <div className="package-title">
          <Icon name={item.role} size={17} />
          <h3>{item.title}</h3>
          <span className="arch">{item.arch}</span>
        </div>
        <p>{item.description}</p>
      </div>
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
      <small className="package-meta">
        v{item.version} <span>·</span> {size}
      </small>
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
    </li>
  );
}

function Downloads({ selected, setSystem }) {
  const content = useRef(null);
  const rail = useRef(null);
  useLayoutEffect(() => {
    const list = rail.current;
    const active = list.querySelector('[aria-selected="true"]');
    function placeMarker() {
      list.style.setProperty("--marker-x", active.offsetLeft + "px");
      list.style.setProperty("--marker-width", active.offsetWidth + "px");
    }
    placeMarker();
    const observer = new ResizeObserver(placeMarker);
    observer.observe(list);
    for (const tab of list.querySelectorAll("button")) observer.observe(tab);
    return () => observer.disconnect();
  }, [selected]);
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
          ref={rail}
          className="platform-rail"
          role="tablist"
          aria-label="下载平台"
        >
          <span className="selection-track" aria-hidden="true" />
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
    title: "Windows 与 Linux 增量更新优化",
    tag: "desktop-updates-2026.11.27",
    date: "2026-11-27",
    changes: [
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
      "Windows 默认下载仍是已完成升级、修复和卸载重装验证的 0.2.136。想使用应用内更新，可在 Windows 下载列表选择 0.2.137。macOS 和 iOS 未做真机验证，iOS 更新后仍需自行签名。",
    ],
  },
  {
    title: "Android 0.3.225",
    tag: "android-0.3.225",
    date: "2026-11-27",
    changes: [
      "在「设置 → 高级选项 → 检查更新」复用当前 APK 下载新版本，由系统确认安装。旧版需从 GitHub 覆盖安装一次。",
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

function Changelog() {
  return (
    <section id="changelog" className="wrap changelog" aria-labelledby="changelog-title">
      <div className="changelog-heading">
        <h2 id="changelog-title">更新记录</h2>
        <a href={repository + "/releases"}>所有平台的发布记录 <Icon name="external" size={13} /></a>
      </div>
      <div className="changelog-list">
        {updates.map(({ title, tag, date, changes }) => (
          <article key={tag}>
            <time dateTime={date}>{date.replaceAll("-", ".")}</time>
            <h3>
              <a href={repository + "/releases/tag/" + tag}>
                {title} <Icon name="external" size={13} />
              </a>
            </h3>
            <ul>{changes.map((change) => <li key={change}>{change}</li>)}</ul>
          </article>
        ))}
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
        <Changelog />
        <ProductFilm player={player} />
      </main>
      <footer className="wrap footer">
        <span>© {edition.date.slice(0, 4)} ATIPiu Labs</span>
        <a href="https://svs.gsfc.nasa.gov/4720/">月面数据 · NASA SVS</a>
      </footer>
    </div>
  );
}
