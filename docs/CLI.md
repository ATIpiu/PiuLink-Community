# PiuLink Windows CLI

Windows Client 的 MSI 和便携包附带 `piulink.exe`。Windows Host 的 MSI 附带 `piulink-host.exe`。从各自的安装目录运行，或在 PowerShell 中使用完整路径。便携包使用解压后的目录。

```powershell
$client = "$env:ProgramFiles\ATIPiu Labs\PiuLink Client\piulink.exe"
$hostCli = "$env:ProgramFiles\ATIPiu Labs\PiuLink Host\piulink-host.exe"
& $client --help
& $hostCli --help
```

## 客户端

| 命令 | 用途 |
| --- | --- |
| `hosts [--json]` | 读取当前 Windows 用户在 PiuLink Client 保存的主机列表与配置 ID。 |
| `discover [--json]` | 在局域网发现 Host。 |
| `probe <host> [--json]` | 检查 Host 的 GameStream TCP 端口。 |
| `pair <host>` | 生成一次性四位 PIN，在 Host 端确认。 |
| `apps <host> [--json]` | 列出已配对 Host 发布的应用。 |
| `displays <host> [--json]` | 通过配对证书读取 Host 的屏幕、设备 ID 和支持的模式。 |
| `stream <host> <app> [选项]` | 启动串流窗口，等待窗口关闭。 |
| `quit <host>` | 请求 Host 结束当前应用。 |
| `wake <host> [mac]` | 使用保存的 MAC 地址或传入的 MAC 地址发送 Wake-on-LAN 包。 |

`<host>` 支持保存的名称、配置 ID、IPv4、IPv6 或主机名，包括 Tailscale 地址。重名时用 `hosts` 列出的 ID。指定端口时用 `host:port` 或 `[IPv6]:port`。配对身份与同一 Windows 用户下的 PiuLink Client 图形界面共用。首次配对时保持客户端命令运行，在 Host 界面输入 PIN，或在 Host 的另一个终端运行 `piulink-host pair "设备名"`。

```powershell
& $client discover --json
& $client pair my-host
& $client apps my-host
& $client displays my-host --json
& $client stream my-host Desktop --resolution 1920x1080 --fps 60 --bitrate 20000
```

`stream` 默认读取保存的串流配置，直接使用未保存的地址时采用客户端默认值。可用 `--resolution`、`--fps`、`--bitrate`（Kbps）和 `--display-id` 覆盖单次连接。关闭窗口或按 Ctrl+C 断开本地串流，需要结束 Host 上的应用时执行 `quit`。

## 主机

在运行 Host 的电脑上，以设置过 Sunshine 安全控制的 Windows 用户运行命令。

| 命令 | 用途 |
| --- | --- |
| `doctor [--json]` | 检查引擎、网络、防火墙与显卡。 |
| `status [--json]` | 查看发布应用数、配对客户端数、当前活动串流与保留应用状态。 |
| `pair <name>` | 从终端或标准输入读取客户端显示的四位 PIN，提交本机配对。 |
| `end-stream` | 结束当前 Sunshine 应用和串流。 |
| `repair-desktop` | 调用 Host 的桌面恢复流程。 |

```powershell
& $hostCli doctor --json
& $hostCli status
& $hostCli pair "客厅电脑"
```

支持 `--json` 的命令将 JSON 输出到标准输出，错误写到标准错误。退出码 `0` 表示成功，`1` 表示执行失败，`2` 表示 `doctor` 检查未就绪或 `probe` 未连通，`64` 表示用法错误，`130` 表示取消正在等待或串流的命令。

## 从源码运行

```powershell
dotnet run --project apps/windows-host/src/PiuLink.Host.Cli -c Release -- status --json
dotnet run --project apps/windows-client/src/PiuLink.Client.Cli -c Release -- hosts --json
```

从源码运行依赖 Qt 引擎的命令时，可将 `PIULINK_MOONLIGHT_QT_EXE_PATH` 指向 PiuLink Client 包内的 `moonlight-qt\PiuLink-Moonlight.exe`。发布包中的 CLI 会自动找到一同打包的引擎。
