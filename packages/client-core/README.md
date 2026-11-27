# PiuLink Client Core

Windows 与 Mac 共用的 .NET 8 库，不依赖 WPF、Avalonia 或 Android。

```text
src/PiuLink.Client.Core/
  Models/       主机与串流配置
  Networking/   地址解析、可达性探测
  Storage/      原子保存的本地主机列表
  Streaming/    Moonlight 命令参数、进程生命周期与输出限额
tests/PiuLink.Client.Core.Tests/
```

从仓库根目录执行：

```powershell
dotnet run --project packages/client-core/tests/PiuLink.Client.Core.Tests -c Release
```

程序集和命名空间保持 `PiuLink.Client.Core`。平台 UI 和平台专用逻辑由应用持有；此库不应反向引用 `apps/`。测试为控制台程序，需用 `dotnet run`，不能把 `dotnet test` 的零测试输出当作验证通过。
