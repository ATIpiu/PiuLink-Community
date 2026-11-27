# PiuLink 原生虚拟麦克风

Windows x64，目标为 Windows 10 2004（19041）及以上。设备硬件 ID 为 `ROOT\PiuLinkMic`，内核服务为 `PiuLinkMic`。安装程序保留 0.2.100 创建的 PiuLink 实例 ID，升级时只重新绑定这个实例。

驱动基于 Microsoft Windows Driver Samples 中的 SimpleAudioSample。构建脚本固定上游提交，应用本目录的补丁。适配器独享的 PCM 队列连接 48 kHz、双声道、16 位的播放和录音端点。队列上限为 40 毫秒，缓冲不足时补零，停用或重新开始串流音频时清空。播放端点排除于系统默认扬声器选择之外。上游代码和本目录的驱动修改沿用随附的 Microsoft Public License。

## 构建

构建机需要 VS 2022 x64 C++ 工具和 Windows SDK 10.0.26100.0。脚本通过 NuGet 获取固定版本的 WDK。本地已有上游仓库时，可通过 `-UpstreamRepository` 指定；脚本始终从固定提交导出干净源码。

```powershell
./apps/windows-host/audio-driver/Build-Driver.ps1
```

`artifacts/PiuLinkMic-x64` 包含 SYS、INF、未签名的 CAT、符号文件和带子目录的提交 CAB（INF、SYS、PDB、CAT）。每次构建执行 InfVerif 和 Inf2Cat。源代码级队列回归测试位于 `tests/PcmQueueTest.cpp`，可用 MSVC 在用户态运行。

## 签名和集成

测试签名使用带子目录的 CAB，由已注册的组织 EV 代码签名证书签署后提交硬件中心。正式发行按 WHCP 要求运行目标系统的 HLK 测试、生成并签署 .hlkx，再提交认证。请在团队的签名环境中操作私钥和硬件开发者账户。[微软驱动签名选项](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings)

取回微软签名包后，将其中匹配的 INF、SYS、CAT 传给 `apps/stream-engine/Build-IntegratedHost.ps1 -MicrophonePackageDirectory <目录>`。默认目录为 `artifacts/PiuLinkMic-signed-x64`。构建会核对微软目录签名及两个文件的目录成员关系，并将三者嵌入 MSI。终端用户安装时无需下载驱动或安装 Steam。开发机可以通过 `-Development -MicrophoneLayoutTest` 仅验证 MSI 结构，这种安装包会在 Windows Installer 启动条件检查处停止。

## 待签名包的实机验收

2026-11-21 已在构建机验证 x64 驱动编译、InfVerif（/w 和 /h）、Inf2Cat、PCM 队列 5000 次随机操作和 MSI 内三份驱动文件的 SHA-256。Host 0.2.101 的安装辅助程序已在交互会话中保存、读回六个声音默认角色。使用本机已装的 0.2.100 旧设备，0.2.101 的切换逻辑通过三个录音角色的切换与恢复测试，测试音 600 Hz、1100 Hz 均成功回录。这项回录使用旧驱动。

在无 Steam 的干净 x64 Windows 上安装签名 MSI，记录安装前后的默认录音与播放角色，确认 PiuLink 录音设备在首次启动 Host 前即已出现。用平板连接后，让普通 Windows 录音应用打开系统默认输入，核对按需提示和实际波形。断开并重新连接，确认之前的默认设备得到恢复、手动切换受到保留、设备实例没有重复。另从 0.2.100 升级，覆盖安装失败回滚、修复安装和卸载。上述实机步骤仍待正式签名包。

在空闲主机上可用以下命令复测切换和回录。测试会短暂切换系统默认录音角色，读回原设备后进行双音测试。

```powershell
dotnet run --project apps/windows-host/tests/PiuLink.Host.Ssh.Tests -c Release -- --microphone-device-test
```
