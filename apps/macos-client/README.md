# PiuLink Mac 客户端 0.1.7（预览版）

**未在 Mac 真机上验证。** 主播目前预算有限，手头没有 Mac 可供测试。

可连接 Windows 和 Linux Host，支持邀请链接 P2P 打洞。粘贴 Host 的完整邀请会自动选择相应打洞服务；在「管理主机」也可选择「用邀请链接打洞」。支持保存主机、PIN 配对、应用列表、窗口或全屏串流。重新打开时会选中最近连接的主机，恢复它的串流设置和上次使用的应用，可直接开始串流。点「刷新应用」可获取主机当前发布的列表。Linux 客户端与 Mac 共用这个界面。

## 使用

1. macOS 12 或更新版本。选择 `osx-arm64`（Apple Silicon）或 `osx-x64`（Intel）包。
2. 从 [Moonlight 官方网站](https://moonlight-stream.org/) 安装 Moonlight.app 到 `/Applications`。此预览包不内置 Moonlight；PiuLink 使用它的命令行接口启动串流。可在“串流引擎”中填写其他可执行文件路径，或设置 `PIULINK_MOONLIGHT_PATH`。
3. 解压，将 PiuLink.app 放到“应用程序”。在「管理主机」粘贴 Host 复制的直连邀请链接，也可填写局域网 IP 或 Tailscale 地址。手动输入地址时可指定 Sunshine HTTP 端口，默认 47989。
4. 点击首次配对，将显示的四位码输入 Host 的配对页面。之后刷新应用、选择应用并连接。

主机列表保存于 `~/Library/Application Support/PiuLink/hosts.json`。配对凭据由 Moonlight 管理。窗口关闭不会强制终止已运行的串流。配对等待最多 120 秒，应用列表等待最多 20 秒；失败后可重试。保存文件损坏时会禁止覆盖，需修复该文件后重新启动。

## 构建与发布状态

```powershell
pwsh ./apps/macos-client/installers/Build-Mac.ps1 -OutputDirectory ./artifacts/mac-build
```

需要 .NET 8 SDK 与 PowerShell 7。脚本在 Windows 或 macOS 上交叉发布两个架构，包含 .NET 运行时，打包 `.app` 并保留 ZIP 中的 Unix 执行权限。重复构建应指定新的输出目录。

当前为交叉编译的未签名预览包，尚未在 Mac 上验证窗口、键鼠、配对和视频解码。未完成 Developer ID 签名、公证或正式分发，不能视为即装即用的正式发行版。发布前必须在 Mac 上检查原生库和 apphost 签名，完成签名与公证并跑完整串流验收；不要要求用户关闭系统安全保护。

打包参考：[Avalonia macOS 部署文档](https://docs.avaloniaui.net/docs/deployment/macos)。

## 依赖

- Avalonia 11.3.19（MIT）及其 NuGet 传递依赖，包括 SkiaSharp；准确版本见源码目录 `src/PiuLink.Mac/packages.lock.json`。
- .NET 8 runtime（MIT）随 self-contained 包分发。
- 外部安装的 Moonlight Qt（GPLv3）不在此包中分发。

首次配对在 Windows Host 本机确认 PIN。此客户端复用 PiuLink.Client.Core 的地址校验、Qt 启动参数和主机列表存储。
