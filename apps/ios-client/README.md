# PiuLink iPhone 客户端 0.1.1 开发版

**未在 iPhone 或 iPad 真机上验证。** 主播目前预算有限，手头没有用于测试的苹果设备。提供未签名的 ARM64 IPA。安装到设备前需自行签名，也可在 Mac 上编译源码。

以固定提交的 [Moonlight iOS](https://github.com/moonlight-stream/moonlight-ios/tree/02dc9780496eeeac6d01c8bbdccb8b6fe71ef28a) 为基础，复用原生发现、证书配对、应用库、视频解码和触控输入。PiuLink 补丁提供独立的 iOS 应用标识、主屏图标、Windows/Linux 配对指引和手动添加主机的错误反馈。首次联调在同一局域网进行。

在 Mac 上从仓库根目录运行：

```sh
python3 apps/ios-client/prepare.py work/piulink-iphone
open work/piulink-iphone/Moonlight.xcodeproj
```

选择 iOS 的 `Moonlight` scheme。模拟器可用以下命令编译。在 iPhone 上运行时，在 Signing & Capabilities 中选择自己的开发团队，使用已注册的 `labs.atipiu.piulink.ios` 或团队可用的开发标识。

```sh
xcodebuild -project work/piulink-iphone/Moonlight.xcodeproj -scheme Moonlight \
  -configuration Debug -sdk iphonesimulator CODE_SIGNING_ALLOWED=NO build
```

源代码和补丁沿用上游 GPL-3.0 许可，第三方组件的许可和固定版本保存在准备好的源码树中。

Add Host 会从链接中识别服务，尝试可达地址，必要时启动 ICE 打洞并用原配对身份校验 Host。可粘贴完整的 [PiuLink 直连邀请](../../docs/rendezvous.md)。

下载页的 `PiuLink-iOS-0.1.1-arm64-unsigned.ipa` 由 Xcode 使用 Release 配置和 iPhoneOS SDK 构建，适用于 iOS / iPadOS 15 及以上。安装到设备前需自行签名。博主手边没有 iPhone 或 iPad，尚未验证真机安装、配对、触控和串流。[本版构建记录](https://github.com/ATIpiu/PiuLink-Community/actions/runs/45538179571)
