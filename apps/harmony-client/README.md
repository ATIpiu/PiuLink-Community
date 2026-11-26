# PiuLink 鸿蒙原生客户端 0.1.0 开发版

本工程使用固定版本的 Moonlight Harmony 原生 ArkTS 界面、配对、音视频和输入模块。支持手机和平板的局域网地址配对。首次使用时在 PiuLink Host 查看本机地址，在鸿蒙端手动添加，并在被控端确认 PIN。邀请链接、ICE 和超级屏尚未集成。

从仓库根目录准备源码（Git Bash 或 Linux）：

```sh
git clone https://github.com/AlkaidLab/moonlight-harmony.git work/piulink-harmony
git -C work/piulink-harmony checkout --detach f9aabcc9d44234d7b9abd5927af855952aaa4283
git -C work/piulink-harmony submodule update --init --recursive
git -C work/piulink-harmony apply ../../apps/harmony-client/piulink.patch
```

原生库构建目标包含 arm64-v8a 和 x86_64。GitHub 构建产物用于开发验证，设备签名和模拟器首帧需另外验证。第三方源码及许可证保留在固定提交中，补丁遵循上游 GPL-3.0。
