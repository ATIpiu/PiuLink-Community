# Windows 整合版

整合版在 Windows x64 上一次安装 PiuLink Host 和 PiuLink Client。需要单一角色时，可以单独下载对应的 MSI。

先构建公开预览版 Host MSI 和 x64 Client MSI，再从仓库根目录运行以下命令。

```powershell
./apps/windows-suite/Build-WindowsSuite.ps1 -HostMsi <Host.msi> -ClientMsi <Client.msi>
```

生成的 EXE 内含两个完整的 MSI，安装时可以离线。默认版本号取自 Host。如果只更新 Client，可通过 `-SuiteVersion` 指定递增的整合版版本号。
