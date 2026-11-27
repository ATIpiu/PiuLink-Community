# Android 源码位置

公开版直接包含 [Android 工程](../../piulink-android/README.md)、[修改的 moonlight-common-c](../../piulink-android/app/src/main/jni/moonlight-core/moonlight-common-c) 和 [enet](../../piulink-android/app/src/main/jni/moonlight-core/moonlight-common-c/enet)。它们在同一版本树中，可以从一次普通 Git 克隆或完整源码 ZIP 构建。Go 直连传输和协议测试使用主工程的 `packages/direct-tunnel/` 和 `contracts/`。

Android 工程源自 Moonlight Android 与 Moonlight Skylark。开发检出采用的原生核心基线为 `50b488b3a20184a9ea4f35a969f106c95079fa96`，公开树包含后续本地集成修改。enet 基线为 `d3a323fc8b9559786ee059205db74e8027c756f7`。各次公开发布以本仓库标签中的实际源码为准。[构建步骤](../../docs/DEVELOPMENT.md) · [上游信息](UPSTREAM.md)
