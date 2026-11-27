# PiuLink 鸿蒙原生客户端 0.1.4 开发版

本工程使用固定版本的 Moonlight Harmony 原生 ArkTS 界面、配对、音视频和输入模块。支持手机和平板的局域网地址配对。首次使用时在 PiuLink Host 查看本机地址，在鸿蒙端手动添加，并在被控端确认 PIN。邀请链接、ICE 和超级屏尚未集成。

从仓库根目录准备源码（Git Bash 或 Linux）：

```sh
git clone https://github.com/AlkaidLab/moonlight-harmony.git work/piulink-harmony
git -C work/piulink-harmony checkout --detach f9aabcc9d44234d7b9abd5927af855952aaa4283
git -C work/piulink-harmony submodule update --init --recursive
(cd work/piulink-harmony && bash ../../apps/harmony-client/prepare.sh)
```

原生库构建目标包含 arm64-v8a 和 x86_64。GitHub 构建产物用于开发验证，模拟器测试结果见下文，零售设备签名与实机验收待完成。第三方源码及许可证保留在固定提交中，补丁遵循上游 GPL-3.0。

## 2026-11-27 模拟器验证

在 DevEco Studio 6.1.1.460 的 Pura 90 Pro x86_64 手机模拟器（HarmonyOS 6.1.1，API 24）上，通过局域网连接实际 Windows PiuLink Host。HAP 0.1.0 完成首次 PIN 配对并读取两个应用。覆盖安装 0.1.1 后保留配对，在 H.264 下打开桌面。720p 的连续三个画面显示测试窗口的计数递增，Windows 收到触控板点击和键盘字符。在 1080p、60 FPS 配置下，解码器记录到 1920×1080 首帧，音频渲染器启动。另一次 4K、60 FPS 配置的会话记录到 3840×2160 首帧。上述结果未测量持续帧率或音质。

断开连接后可恢复桌面会话，退出游戏后 Host 的活动串流数和应用 ID 均为零。13 个设置分组均已展开并回读内容，测试覆盖视频自适应码率和音频兼容模式的打开与恢复。运行 test-settings.py 前需在该型号的竖屏模拟器打开设置并折叠所有分组。通过 --hdc、--target 和 --output 指定调试工具、模拟器连接与结果 JSON。另用 HarmonyOS 6.0.1（API 21）手机模拟器对 0.1.1 执行扩展回归，13 个分组均能打开，25 个开关逐一切换、回读并恢复。部分设置依赖实物，其开关回读仅证明客户端状态发生了变化。

在被控 Windows 电脑本机同时运行模拟器时，默认 Direct3D 路径多次导致模拟器进程退出。一次崩溃转储的故障模块为 d3d11.dll。只对 Emulator.exe 使用 Windows WARP 后可重复连接和接收画面。本次 x86 镜像报告 H.264 可解码，HEVC 和 AV1 不可用。ARM 真机硬解、HDR、实体手柄、USB 振动、麦克风回传和多声道效果均待对应设备验证。


设置中的「关于 → 检查更新」通过 GitHub 获取官方 HAP。下载时复用可读取的已安装 HAP 或本机缓存，也可以选择旧 HAP。下载后显示实际安装包流量与复用量，并可在系统文件选择器中保存。实体设备安装时需沿用原签名。
