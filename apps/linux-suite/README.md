# PiuLink Linux 整合版

支持 Ubuntu 26.04 的 x86-64 桌面。一份 DEB 提供 Linux 控制端、被控端和共用的 .NET 运行时。Linux ARM64 可使用独立控制端。

## 安装

在本地开发包所在目录运行 `sudo apt install ./piulink-suite_0.1.1-1_amd64.deb`。已有 Debian 格式的 PiuLink 控制端或被控端会由 apt 替换，原桌面用户的设置、电脑列表和配对凭据继续使用。

安装后可直接从应用菜单打开 PiuLink 控制端。DEB 内置使用 Rust 媒体核心的接收引擎。启用本机被控端时，以桌面账户从应用菜单打开 **PiuLink 串流组件设置**，或运行 `piulink-suite-setup`，首次设置会联网下载并校验主机运行时。

需要让其他设备连接本机时，在 X11 桌面会话里运行以下命令。使用无显示器的软件编码桌面时，将第一行末尾的 `desktop` 改为 `headless`。

```sh
piulink-host setup desktop
systemctl --user enable --now piulink-host.service
```

## 更新和移除

控制端的「检查更新」与 `piulink-host update check` 识别同一个整合版更新通道。下载并校验完成后，用 apt 安装新的 DEB。用 `sudo apt remove piulink-suite` 可以卸载，用户目录里的配置和凭据会保留。包管理器安装的依赖和首次下载的组件占用额外磁盘空间。

## 构建

在 Ubuntu 26.04 x64 上，从包含递归子模块的 Qt v6.2.0 源码检出构建接收引擎。使用独立检出，构建脚本会应用 PiuLink 窗口和图标补丁。运行 `python3 apps/linux-client/build-engine.py <源码检出> <构建目录> <payload目录>`，将下述两个 .NET 发布目录和 X11 库放入同一 payload。

以自包含模式分别发布 `apps/macos-client/src/PiuLink.Mac` 与 `apps/linux-host/src/PiuLink.Host.Linux`，目标为 `linux-x64`，输出到同一目录下的 `client/` 和 `host/`。在 Ubuntu 26.04 用 `apps/linux-host/build-x11-input.sh` 将 `libpiulink-x11-input.so` 编译到该目录。在 Linux 上运行 `tools/build-linux-deb.py --suite-apps <该目录> --packages <输出目录> --licenses licenses --client-notices <第三方声明目录>`。
