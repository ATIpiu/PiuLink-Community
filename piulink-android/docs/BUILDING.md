# Android 构建说明

克隆 [PiuLink 公开仓库](https://github.com/ATIpiu/PiuLink-Community)，或解压同一 Release 的完整源码包。Android 及其原生依赖位于 `piulink-android/`，下列命令从主工程根目录开始执行。

本工程使用 Gradle wrapper 8.13、Android Gradle Plugin 8.13.2、SDK Platform 36 和 NDK 27.0.12077973。Java 编译语言级别为 17。Go 直连库使用主工程 `packages/direct-tunnel/go.mod` 固定的工具链。将 Android SDK 路径写入本地 `local.properties` 的 `sdk.dir`。

## 签名与构建

按 `keystore.properties.example` 配置自己的本地签名信息，或使用 Gradle 属性、`PIULINK_UPLOAD_*` 环境变量。在主工程根目录运行：

```powershell
pwsh ./packages/direct-tunnel/build.ps1 -Android
cd piulink-android
.\gradlew.bat :app:verifyReleaseSigningConfiguration :app:testNonRootReleaseUnitTest :app:lintNonRootRelease :app:assembleNonRootRelease
```

通用 APK 位于 `app/build/outputs/apk/nonRoot/release/`，文件名为 `PiuLink-Android-<版本>-nonRoot-universal-release.apk`，应用 ID 为 `labs.atipiu.piulink`。正式分发前核对 APK 签名、包名、版本名、版本码和支持的 ABI。需要商店 AAB 时使用 `:app:bundleNonRootRelease`。签名私钥和本地配置由构建者保管。

分发 APK 或 AAB 时提供匹配的 Android 与主工程源码、Go 传输库、原生子模块、构建脚本与第三方许可证。[GPLv3](../LICENSE.txt) · [依赖声明](../NOTICE.md)
