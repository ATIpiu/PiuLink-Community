# 开发与验证

## Windows 与 .NET

从主工程根目录使用 PowerShell 7 和 .NET 8 SDK。解决方案涵盖 Windows Host、Windows / Mac Client、共享库和历史激活服务的项目及测试。

```powershell
pwsh ./tools/Check-Project.ps1
pwsh ./tools/Check-Project.ps1 -Build -Tests
```

也可以单独构建平台项目：

```powershell
dotnet build apps/windows-host/PiuLink.Host.sln -c Release
dotnet build apps/windows-client/PiuLink.Client.sln -c Release
dotnet build apps/macos-client/src/PiuLink.Mac/PiuLink.Mac.csproj -c Release
dotnet run --project packages/client-core/tests/PiuLink.Client.Core.Tests -c Release
```

根解决方案包含 Windows 专属项目。在 macOS 上直接构建 Mac 客户端项目和共享库。项目验证与安装、部署分开执行。

## Android

Android 工程在本仓库的 `piulink-android/`。修改过的 moonlight-common-c 和 enet 源码直接随发布树提供；普通克隆即可取得。见[Android 源码说明](../apps/android/ANDROID_REPOSITORY.md)。

准备 JDK（本项目开发环境使用 21）、Android SDK 36、NDK 27.0.12077973 和 Go 工具链。在 Android 工程的本地 `local.properties` 中设置 `sdk.dir`，用自己的签名密钥配置发行构建。从主工程根目录运行：

```powershell
pwsh ./packages/direct-tunnel/build.ps1 -Android
cd piulink-android
.\gradlew.bat :app:verifyReleaseSigningConfiguration :app:testNonRootReleaseUnitTest :app:lintNonRootRelease :app:assembleNonRootRelease
```

通用发行包在 `app/build/outputs/apk/nonRoot/release/`。确认应用 ID 为 `labs.atipiu.piulink`，并核对版本名、版本码和签名。

## 平台打包

| 目标 | 入口 |
| --- | --- |
| Windows 集成 Host | [引擎、安装器与验证](../apps/stream-engine/README.md) |
| Windows Client | [Windows 客户端](../apps/windows-client/README.md) |
| macOS | [Mac 客户端](../apps/macos-client/README.md) |
| Linux Host / Client | [主机端](../apps/linux-host/README.md) · [客户端](../apps/linux-client/README.md) |
| iPhone | [源码套件与 Xcode](../apps/ios-client/README.md) |

发布附件按组件记录版本、架构、签名、对应源码和 SHA-256，详见[发布约定](PUBLICATION.md)。
