# PiuLink Android 0.3.264

本次 Rust 主线预览版包含 0.3.262 引入的串流底部快捷键栏，Android 版本码为 284。

- 串流时可直接点按 Ctrl+C、Ctrl+V、Alt+Tab、Esc 等 11 个快捷键。打开软键盘时仍可使用。
- 在设置的「底部快捷键栏」中选择自动、始终显示或关闭。自动模式会在连接实体键盘时隐藏快捷栏，拔下键盘后恢复。
- 已安装旧版的用户可在 PiuLink 设置中选择「检查更新」。直接安装请选择[通用 APK](https://github.com/ATIpiu/PiuLink-Community/releases/download/android-0.3.264/PiuLink-Android-0.3.264-nonRoot-universal-release.apk)，确认设备为 64 位 ARM 时也可选用较小的 [ARM64 APK](https://github.com/ATIpiu/PiuLink-Community/releases/download/android-0.3.264/PiuLink-Android-0.3.264-nonRoot-arm64-v8a-release.apk)。

两份 APK 均为正式签名的 nonRootRelease，包名为 `labs.atipiu.piulink`。通用版与已在实体平板连接 Windows 主机的版本哈希一致。ARM64 精简版单独构建并校验了签名与架构；134 项 Android 单元测试通过。安装包校验值见本次发布附件 `SHA256SUMS.txt`。
