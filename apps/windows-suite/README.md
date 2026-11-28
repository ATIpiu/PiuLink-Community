# Windows 整合版

整合版在 Windows x64 上一次安装 PiuLink Host 和 PiuLink Client。首次安装、升级和本机安装验收统一使用整合版 EXE。Host 和 Client 的 MSI 仅用于构建和校验整合包。

先构建公开预览版 Host MSI 和 x64 Client MSI，再从仓库根目录运行以下命令。

```powershell
./apps/windows-suite/Build-WindowsSuite.ps1 -HostMsi <Host.msi> -ClientMsi <Client.msi>
```

生成的 EXE 支持离线安装。构建脚本按 SHA-256 识别两端相同的文件，为 Host 和 Client 生成公共 CAB 和各自的专用 CAB。压缩媒体使用新的 MSI 产品标识。通过原有升级标识替换等版本的独立安装媒体，保留组件、文件路径和配置，以便系统使用新媒体修复和卸载。每个角色使用独立的安装缓存，Windows Installer 可以从本机缓存执行维护。遇到已签名的输入 MSI 或包含特殊安装媒体的 MSI 时，构建脚本会直接嵌入原包。

整合版默认沿用 Host 的版本号。单独更新 Client 或整合版封装时，可通过 `-SuiteVersion` 指定递增的版本号。0.2.136 使用 Host 0.2.135 与 Client 0.2.36，两份原始 MSI 合计 180.3 MiB，整合版 EXE 为 118.5 MiB。

以下命令从整合版还原两端的安装媒体，对比原始 MSI 的安装表，再通过 Windows Installer 管理安装核对每个文件的路径和 SHA-256。验证目录须是一个新目录，测试日志保存在其中。

```powershell
./apps/windows-suite/Test-WindowsSuite.ps1 -Bundle <Suite.exe> -HostMsi <Host.msi> -ClientMsi <Client.msi> -WorkDirectory <新目录>
```

从整合版 0.2.140 起，构建环境需要 CMake、Visual Studio 2022 的 MSVC x64 工具和 Windows SDK。还原项目时会取得与 WiX 6 配套的引导程序头文件。

升级或修复时，安装器在替换各组件前记录当前 Windows 会话中正在运行的已安装 Host 或 Client。完成替换后，如果 Windows 重启管理器尚未恢复相应进程，就由桌面会话重新启动它。Host 回到托盘。原先未运行的组件不会启动，卸载不会触发恢复。这样也可以从尚未注册 Windows 应用重启的旧版本升级。
