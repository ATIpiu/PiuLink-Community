# PiuLink iPhone 客户端 0.1.0 开发版

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

Add Host 支持粘贴完整的 [PiuLink 直连邀请](../../docs/rendezvous.md)。邀请补丁已在固定版源码上检查应用；本次 Windows 工作区未运行 Xcode 或 iPhone 真机测试。
