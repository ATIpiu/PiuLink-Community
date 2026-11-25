# PiuLink Linux 客户端

在 Debian 或 Ubuntu 上，先运行 `dpkg --print-architecture` 确认架构。在下载目录执行与设备匹配的一条命令。

```sh
sudo apt install ./piulink-client_0.1.7-1_amd64.deb
# ARM64 设备
sudo apt install ./piulink-client_0.1.7-1_arm64.deb
```

安装后从应用菜单打开 PiuLink，也可以运行 `piulink-client`。串流引擎 Moonlight Qt 需单独安装。其他发行版可使用下载页的便携压缩包。

在「管理主机」粘贴 Windows Host 的邀请链接，选择「用邀请链接打洞」可强制使用 P2P；同一条链接也会带入自建打洞服务地址。已配对的设备复用原 Moonlight 身份。

Linux 与 Mac 共用 Avalonia 桌面界面和 PiuLink.Client.Core。支持保存多台主机、PIN 配对、刷新应用、窗口或全屏串流，并按主机记住上次启动的应用和画质设置。

在有图形桌面的 Linux 上安装 Moonlight Qt，使 `moonlight` 在 PATH 中可用，或在串流设置中填写其可执行文件的绝对路径。解压后运行 `./PiuLink/PiuLink.Linux`。添加局域网或 Tailscale 地址，在 Host 上确认首次配对码，然后刷新应用。配置默认保存在 `~/.config/piulink`，日志在 `~/.local/state/piulink/logs`。

本机装有 PiuLink Linux Host 时，可在「文件传输」中向配对的手机或平板发送多个文件。串流期间也能单独打开传输窗口。停止发送或传输中断后，保留窗口，点「继续发送剩余」即可从首个未收到保存回执的文件开始。点「结束这批」可以重新选择收件人和文件。Linux Host 0.1.2 起可查看每台设备的文件授权状态。

用 .NET 8 SDK 和 Python 3 从仓库根目录构建自包含压缩包。构建默认面向 x86-64，也可传 `--runtime linux-arm64`。

```sh
python3 apps/linux-client/build.py --output artifacts/linux-client
```
