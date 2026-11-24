# PiuLink Android

PiuLink Android 把已配对电脑的桌面和游戏串流到 Android 手机、平板和电视。支持触控、键鼠与手柄。移动端还提供可配置的桌面快捷栏、按网络调整码率、文件互传、远程麦克风和 SSH 终端。功能取决于对端 Host 和设备能力。

## 连接与下载

在局域网里发现或手动添加 Host，完成 PIN 配对后选择桌面或应用。异地可以使用自己的 Tailscale 网络，也可以在兼容版本上粘贴 PiuLink 私有直连邀请。邀请服务协商地址，PiuLink 不提供媒体中转。

[下载 Android 0.3.185 正式签名通用 APK](https://github.com/ATIpiu/PiuLink-Community/releases/download/client-preview-2026.11.25/PiuLink-Android-0.3.185-nonRoot-universal-release.apk)。应用 ID 为 `labs.atipiu.piulink`，版本号为 205。

## 构建

Android 及原生核心、enet 源码已随本仓库提供。Go 直连传输的源码和许可证位于主工程的 `packages/direct-tunnel/`；协议测试使用主工程的 `contracts/`。配置本地 SDK 和自己的签名信息后，从主工程根目录执行：

```powershell
pwsh ./packages/direct-tunnel/build.ps1 -Android
cd piulink-android
.\gradlew.bat :app:verifyReleaseSigningConfiguration :app:assembleNonRootRelease
```

工具版本、签名配置与产物位置见[构建说明](docs/BUILDING.md)。构建自己的发行包请使用自己的签名密钥。

本客户端基于 Moonlight Android 和 Moonlight Skylark，按 GPLv3 分发。请保留上游版权与依赖声明，公开发行 APK 时提供与该版本对应的完整源码、原生子模块和构建脚本。[许可证](LICENSE.txt) · [第三方声明](NOTICE.md)
