# PiuLink Rust 串流引擎

此目录是公开 main 的引擎源码，后续改动均在本仓库迭代。引擎与适配器以 GPL-3.0-only 发布，参见 LICENSE.txt 和仓库的第三方声明。

| 目录 | 职责 |
| --- | --- |
| src | 实时编码码率控制及传输原型 |
| integration/sunshine | Windows 采集、编码、音频、会话和发送端原生适配 |
| integration/wire | GameStream 协议、媒体封包、FEC、缓冲、输入与音视频收发 |
| integration/portable | 桌面与移动接收端共用 Rust 静态库 |
| integration/android | JNI 和 Android NDK 构建适配 |
| tests | 参考实现和回归测试 |

从仓库根目录运行以下测试。Windows Host 构建入口为 apps/stream-engine/Build-Engine.ps1；Windows 控制端使用 apps/windows-client/engine/Build-Engine.ps1。Linux 接收端使用 apps/linux-client/build-engine.py。安卓构建前用 rustup 添加 aarch64-linux-android、armv7-linux-androideabi、i686-linux-android 和 x86_64-linux-android 四个目标；assembleNonRootRelease 会从源码构建静态库。鸿蒙与 iOS 分别使用 apps/harmony-client/prepare.sh 和 apps/ios-client/prepare.py。

```sh
cargo test --locked --manifest-path engines/rust/Cargo.toml
cargo test --locked --manifest-path engines/rust/integration/portable/Cargo.toml
cargo test --locked --manifest-path engines/rust/integration/wire/Cargo.toml
cargo test --locked --manifest-path engines/rust/integration/sunshine/Cargo.toml
```
