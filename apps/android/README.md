# Android 手机、平板和电视

PiuLink Android 使用标准 Moonlight 串流链路连接自己的电脑，提供局域网发现、手动地址、证书配对、键鼠、触控与手柄。使用私有邀请时，客户端先核对 Host 身份，再尝试直连。[直连邀请](../../docs/rendezvous.md)

## 手机和平板

- 在桌面或游戏中打开快捷栏、键盘或屏幕控制，按设备和网络情况选用码率策略。
- 可以和已配对电脑互传文件。内置 SSH 终端单独验证主机指纹，在支持 tmux 的环境中可重新接入保留的会话。
- Host 配置了兼容的虚拟录音设备时，可将手机麦克风用于远端会议和游戏语音。客户端在授权后采集，进入小窗、后台或断开时停止采音。依赖已有录音设备的 Host 测试包会在发布说明中标明。

## 获取与构建

[下载 Android 0.3.186 通用 APK](https://github.com/ATIpiu/PiuLink-Community/releases/download/android-0.3.186/PiuLink-Android-0.3.186-nonRoot-universal-release.apk)。该包经过正式签名，包名为 `labs.atipiu.piulink`，手机、平板与电视共用。[Android 源码](../../piulink-android/README.md) · [开发指南](../../docs/DEVELOPMENT.md)

在「设置 → 触控 → 触控板鼠标速度」或已连接桌面的串流菜单中选择速度。默认 1×，可在 0.5× 至 4× 之间选择，设置会在本机保存。
