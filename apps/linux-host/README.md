# PiuLink Host for Linux 0.1.4

Linux Host 由用户级 systemd 服务、命令行和 Sunshine 组成。现有 PiuLink 和 Moonlight 客户端可以使用 GameStream 配对与串流。PiuLink 客户端还可以通过 Companion 读取显示信息、发现 SSH 终端并互传文件。Host 无需 PiuLink 账号或激活码。

## 桌面模式

使用 `piulink-host invite-service` 查看当前信令服务，关闭邀请后可用 `piulink-host invite-service https://connect.example.org/piulink/` 保存自建服务地址。此 Linux Host 提供地址登记。

运行中的 Host 可用 `piulink-host invite` 开启并打印直连邀请。多台客户端可以粘贴同一链接。
运行 `piulink-host invite-off` 停止登记，原链接最多再保留 90 秒。参见[直连邀请](../../docs/rendezvous.md)。

| 模式 | 行为 | 使用条件 |
| --- | --- | --- |
| `headless` | 在独立的 Xvfb 屏幕上运行 Openbox 和终端。默认 1920×1080，使用 CPU 编码，串流键鼠事件通过 XTEST 进入该桌面。 | 用户级 systemd、Xvfb、X11 工具和私有输入适配库。主机音频使用 PipeWire 的 PulseAudio 接口及专用虚拟输出。 |
| `desktop` | 采集当前已登录 X11 会话的一个输出。默认让 Sunshine 探测编码器。 | 本机 X11 登录会话、`DISPLAY` 和可访问的 Xauthority。安装了支持的驱动与设备权限时可使用硬件编码。 |

两种模式都在启动时确定采集输出。客户端能够读取该屏幕的名称、坐标和模式。Windows Host 的多屏热切换、独占显示、窗口集中、缩放恢复和虚拟麦克风暂未移植。Wayland 桌面及 GPU 编码需要另做实机验证。HDR 取决于图形会话和编码器，本轮 headless 验证使用 SDR。

服务启动时优先采集 X11 的活动主屏，未指定主屏时使用第一个有画面的输出。可用 `piulink-host capture-index` 检查选择，或用 `PIULINK_OUTPUT_INDEX` 指定 Sunshine 的输出编号。显式指定到断开的端口时，启动会报告原因。

## 构建和安装

Ubuntu 26.04 x86-64 可以在下载目录安装 DEB，然后用桌面用户配置运行环境。下面以采集已登录的 X11 桌面为例。

```sh
sudo apt install ./piulink-host_0.1.4-1_amd64.deb
piulink-host-setup-user
/usr/bin/piulink-host setup desktop
systemctl --user enable --now piulink-host.service
```

`piulink-host-setup-user` 会通过网络下载固定版本的 Sunshine 和 .NET 运行时。如果已有一份手工安装并运行的用户服务，先停止该服务，再配置新版本。

从仓库根目录使用 .NET 8 SDK 构建。发布目录可从 Windows 复制到 Linux。

```sh
dotnet publish apps/linux-host/src/PiuLink.Host.Linux/PiuLink.Host.Linux.csproj \
  -c Release -p:UseAppHost=false -o ./app
```

Ubuntu 26.04 x86-64 自带桌面组件的机器可以使用下面的普通用户安装方式。在 Linux 上把发布目录 `app` 和本目录的脚本放在一起。保持 APT 软件包索引可用，并准备 `curl`、`openssl`、`xauth`、`xrandr`、`flock` 和 `systemctl --user`。

```sh
bash bootstrap-ubuntu-26.04-x64.sh
bash install-user.sh app
~/.local/bin/piulink-host setup headless
systemctl --user enable --now piulink-host.service
~/.local/bin/piulink-host doctor
```

Bootstrap 校验固定版本的 Sunshine AppImage 和 .NET 8 Runtime，将依赖解包到当前用户目录。已有发行版 Sunshine 和 .NET 8 Runtime 的环境可以直接执行 `install-user.sh`。有多套环境时，可用 systemd 用户服务的 `Environment=` 指定 `PIULINK_SUNSHINE`、`PIULINK_DOTNET`、`PIULINK_HEADLESS_DISPLAY`、`PIULINK_HEADLESS_MODE`、`PIULINK_OUTPUT_INDEX` 或 `PIULINK_ENCODER`。

要采集现有 X11 桌面，请在该桌面的终端把模式切到 `desktop`。先停止服务，再执行 `systemctl --user import-environment DISPLAY`。如果该会话设置了 `XAUTHORITY`，把它一并导入，然后启动服务。登录后显示环境发生变化时重新导入。

Headless 模式通过私有 X11 连接接收已配对串流的键盘和鼠标事件。启动时必须能打开 XTEST 并初始化相对鼠标、绝对鼠标和键盘，`doctor` 应回读 `inputBackend: xtest`。手柄和数位笔需要独立的输入后端。实体 X11 桌面继续使用 Sunshine 的标准 `/dev/uinput` 路径，应通过发行版软件包配置权限并实测。 [X.Org XTEST 接口](https://xorg.freedesktop.org/releases/current/doc/libXtst/xtestlib.html) · [Sunshine Linux 安装说明](https://docs.lizardbyte.dev/projects/sunshine/v2026.516.143833/md_docs_2getting__started.html)

安装包自带 Ubuntu x86-64 输入适配库。从源码执行 bootstrap 时需要 `cc`。如已安装 Xlib、libXtst 和 libevdev 的开发包，也可用 `bash build-x11-input.sh <库路径>` 单独构建。库由 Headless 模式的 Sunshine 进程加载，使用已经创建好的私有 Xauthority。

## 连接和管理

- 在同一局域网等待发现，或手动添加 Host 的 LAN 或 Tailscale IPv4 地址。客户端显示配对码时，通过本机或已授权 SSH 运行 `~/.local/bin/piulink-host pin` 并输入该码。
- 普通客户端使用 Sunshine 的应用列表。默认发布 Desktop 和 Terminal。应用表存放在 `config/sunshine/apps.json`，可用 `piulink-host apps` 检查，修改后重启服务以加载变更。
- `piulink-host peers` 列出已配对设备。每个设备的 `transferReady` 表示是否已建立文件授权和保存收件地址。运行 `piulink-host send "Xiaomi M367FC" photo.jpg clip.mp4`，可按设备名称或完整证书指纹发送一批文件。每个文件单独报告传输进度和收件确认。遇到同名设备须用完整指纹指定目标。首次显示等待授权时，在收件设备上打开 PiuLink 并点一下这台 Linux 主机。收到的文件保存在 `~/Downloads/PiuLink`。
- Android 的 SSH 设备授权发出请求后，运行 `piulink-host ssh-pending` 查看设备、来源和公钥指纹。核对设备公钥后，使用 `piulink-host ssh-approve <request-id> <ssh-key-fingerprint>` 进行本机批准。持续终端会话要求 SSH 命令环境中的 `PATH` 能找到 `tmux`。
- `piulink-host clients`、`piulink-host config` 和 `journalctl --user -u piulink-host.service` 可检查已配对设备、有效配置和日志。诊断同时列出网卡、MAC、地址、Tailscale 接口和输入设备权限。

管理 API 由 Sunshine 的本机访问策略限制。Companion 的显示和传输接口用 Sunshine 的同一主机证书，并验证对端仍然是已启用的配对证书。Linux 版使用 IPv4 监听，关闭 UPnP，局域网按客户端能力协商串流加密。SSH 信息与待批准请求兼容当前 Android 客户端的局域网 HTTP 接口，写入 OpenSSH 公钥必须在 Host 本机批准。

本版使用项目固定版本的上游 Sunshine AppImage。Windows 定制引擎的内容自适应码率、显示租约和活动串流遥测还需要 Linux 引擎构建及独立验证。 [本次使用的 Sunshine 版本](https://github.com/LizardByte/Sunshine/releases/tag/v2026.516.143833)

## 停止和保留数据

`systemctl --user disable --now piulink-host.service` 停止并关闭自启动。用户级安装和升级保留 `~/.local/share/piulink-linux` 中的证书、应用设置和传输授权。
