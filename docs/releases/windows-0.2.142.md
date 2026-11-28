# Windows x64 整合版 0.2.142

[下载 Windows x64 整合安装包](https://github.com/ATIpiu/PiuLink-Community/releases/download/windows-0.2.142/PiuLink-Windows-0.2.142-win-x64.exe)

本版将已在 Windows 上验证的 Host 0.2.140、Client 0.2.41 和 piulink.18 Rust 集成引擎打入同一份公开预览安装包。上一份公开整合版 0.2.141 内置 Host 0.2.138。新版统一了主机管理页、引擎文件和托盘的 PiuLink 标识。安装包为 125,086,287 字节。

**如何更新**

- 使用公开整合版 0.2.141 的用户可以在 Host 或 Client 中选择「检查更新」，也可以直接下载安装包。
- 如果使用四段版本号的本地开发版（例如 0.2.140.5），请直接下载本页 EXE。旧开发版的更新器无法识别自身的四段版本号。

**验证范围**

安装包解包后的 1,074 个 Host 文件和 977 个 Client 文件均与对应实测 MSI 的路径与 SHA-256 匹配。在 Windows 上完整替换开发版后，已确认 Host 服务运行、Rust 引擎文件匹配、ShareTarget 签名有效，配对与用户配置校验通过。整合安装包 SHA-256 见本页附件。

Linux 整合版沿用 [Ubuntu 26.04 x64 的 0.1.1-1 DEB](https://github.com/ATIpiu/PiuLink-Community/releases/download/rust-2026.11.28/piulink-suite_0.1.1-1_amd64.deb)。公开文件与完成过 X11 软件编解码串流实测的 DEB 逐字节一致。macOS Rust 整合版暂未发布。
