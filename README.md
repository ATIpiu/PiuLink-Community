# PiuLink

PiuLink 让你从手机、平板、电视或另一台电脑连接自己的电脑，远程使用桌面和应用，也可以用手柄玩游戏。主机端基于 Sunshine，客户端兼容 Moonlight。分辨率、画面比例和帧率取决于显示器、编解码硬件和网络条件。

## 下载与连接

| 使用设备 | 安装包 |
| --- | --- |
| 让其他设备连接这台 Windows x64 电脑 | [下载 Windows Host 0.2.116 · MSI](https://github.com/ATIpiu/PiuLink-Community/releases/download/host-preview-2026.11.25/PiuLink-Host-0.2.116-win-x64-preview.msi) |
| 在 Windows x64 电脑上控制另一台主机 | [下载 Windows Client 0.2.31 · MSI](https://github.com/ATIpiu/PiuLink-Community/releases/download/client-preview-2026.11.25/PiuLink-Client-0.2.31-win-x64.msi) |
| 在 Android 手机、平板或电视上控制主机 | [通用版（推荐）· 56.14 MB](https://github.com/ATIpiu/PiuLink-Community/releases/download/android-0.3.186/PiuLink-Android-0.3.186-nonRoot-universal-release.apk) · [ARM64 专用版 · 17.09 MB](https://github.com/ATIpiu/PiuLink-Community/releases/download/android-0.3.186/PiuLink-Android-0.3.186-nonRoot-arm64-v8a-release.apk) |

两份 Windows MSI 均为未签名预览版。Host 内置串流引擎，这版省去了虚拟麦克风驱动，系统分享扩展可在已信任其签名的设备上按需注册。两个控制端都可连接已配置好的 PiuLink Host 或兼容 Sunshine 的电脑。[Host 版本说明和校验文件](https://github.com/ATIpiu/PiuLink-Community/releases/tag/host-preview-2026.11.25) · [控制端版本说明和校验文件](https://github.com/ATIpiu/PiuLink-Community/releases/tag/client-preview-2026.11.25) · [自建 Linux 邀请服务](https://github.com/ATIpiu/PiuLink-Community/releases/tag/source-preview-2026.11.24)

1. 在要被控制的电脑上安装 **Host**，在操作设备上安装 **Client**。Android 手机、平板与电视默认选通用版。确认系统支持 `arm64-v8a` 时可选更小的 ARM64 专用版，两版功能相同。
2. 第一次连接建议让两台设备使用同一局域网。在客户端发现或手动添加电脑，按提示在 Host 确认配对。
3. 选择「桌面」或已配置的游戏、应用开始串流。Windows Host 与各客户端均可直接启动，局域网使用无需 PiuLink 账号。
4. 异地使用可手动添加可达地址、使用自己配置的 Tailscale，或在支持的版本上使用 Host 生成的私有邀请。邀请服务用于发现地址和协商直连，目前属实验功能。PiuLink 不提供音视频中转。

在 Android 客户端的「设置 → 触控 → 触控板鼠标速度」可选 0.5× 至 4×。串流中也可从菜单直接调速，选择后立即生效。此设置作用于触控板模式下的单指移动与拖动。

全新 Windows 电脑安装 Host 0.2.116 后，麦克风回传还需要适用的 PiuLink 虚拟麦克风驱动。已有 PiuLink 录音设备的电脑可以在 Android 串流菜单点「开启远端麦克风」、授予录音权限，并在远端会议或录音应用中选择相应的输入。[虚拟麦克风驱动与打包说明](apps/windows-host/audio-driver/README.md)

## 各平台能做什么

| 平台 | 用途与进展 |
| --- | --- |
| Windows Host | 捕获桌面和应用、配对客户端、选择实体或虚拟屏、恢复显示布局。支持配对设备传文件和兼容录音设备上的麦克风回传。 |
| Windows Client | 管理主机、启动桌面与游戏、设置串流画质，提供图形界面和命令行。 |
| Android 手机、平板、电视 | 桌面和游戏串流、键鼠、触控、手柄。手机和平板另有桌面快捷栏、文件互传、麦克风回传和 SSH 终端。 |
| Linux Host / Client | 主机端支持 X11 与无头配置；图形客户端通过已安装的 Moonlight Qt 启动串流。具体环境要求见平台说明。 |
| macOS | 未在 Mac 真机上验证。当前提供未签名、未公证的预览包，依赖 Moonlight.app。 |
| iOS | 未在 iPhone 或 iPad 真机上验证。当前仅提供源码，需在 Mac 上用 Xcode 编译和签名。 |

主播目前预算有限，手头没有用于实测的苹果设备。

Windows 主链路和 Android 已有真机串流验证。直连邀请在部分家庭网络上仍会超时；使用已知地址或局域网发现可以独立于邀请服务连接。当前集成引擎标记为开发版本。

## 源码与构建

| 入口 | 内容 |
| --- | --- |
| [Windows Host](apps/windows-host/README.md) · [串流引擎](apps/stream-engine/README.md) | 主机源码、引擎固定版本及补丁、安装器和验证脚本 |
| [Windows Client](apps/windows-client/README.md) · [命令行](docs/CLI.md) | Windows 客户端、Qt 引擎集成、配对与串流命令 |
| [Android](apps/android/README.md) | 随仓库提供的 Android 源码、原生依赖和签名构建入口 |
| [Linux Host](apps/linux-host/README.md) · [Linux Client](apps/linux-client/README.md) · [macOS](apps/macos-client/README.md) · [iPhone](apps/ios-client/README.md) | 平台要求、实现及打包入口 |
| [共享客户端库](packages/client-core/README.md) · [直连传输](packages/direct-tunnel) · [邀请服务](services/rendezvous) | 主机记录、连接协议、客户端与服务端实现 |

在 Windows 安装 .NET 8 SDK、PowerShell 7 和 Go（直连传输使用 Go 1.26.6 工具链）后，从仓库根目录执行：

```powershell
pwsh ./tools/Check-Project.ps1
pwsh ./tools/Check-Project.ps1 -Build -Tests
```

第一条检查项目引用和文档链接，第二条编译并运行现有 .NET 测试。Android 和原生核心、enet 源码已一同收录。详见[开发指南](docs/DEVELOPMENT.md)。

## 许可与发布

PiuLink 自有独立组件按 **PolyForm Noncommercial 1.0.0** 授权，可以按许可进行非商业使用、修改和分享。Moonlight、Sunshine 的衍生程序及其对应源码按 GPLv3 分发，第三方组件沿用各自许可。[各组件的许可范围](LICENSE.md)

在 [GitHub Releases](https://github.com/ATIpiu/PiuLink-Community/releases) 下载版本附件和 `SHA256SUMS`。源码目录、平台版本及发布约定见[源码与 Release 说明](docs/PUBLICATION.md)。
