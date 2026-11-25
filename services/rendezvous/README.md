# 自建 PiuLink 打洞服务

这台服务器负责登记 Host 地址、交换短期 ICE 信令和响应 STUN Binding。串流建立后，画面、声音和输入直接在两台设备间传输。当前协议没有 TURN 中继；两端没有可用的直连路径时，连接会失败。

准备一台装有 systemd、Caddy、Python 3、curl 和 Go 1.22 或更新版本的 Linux 服务器。将自己的域名解析到 VPS，允许入站 TCP 443 和 UDP 3478。使用 Caddy 的 HTTP 证书验证时，还需要开放 TCP 80。使用自定义 HTTPS 端口时，在下面的地址和防火墙中均指定该端口。

在本目录构建并安装。将示例域名换成自己的域名。

```sh
go test ./...
go build -trimpath -o server .
sudo env PIULINK_PUBLIC_URL=https://connect.example.org sh install.sh
systemctl status piulink-rendezvous
curl -fsS https://connect.example.org/piulink/join
```

在 Windows Host 的「连接信息 → 邀请设置」填写 `https://connect.example.org/piulink/`。如已有邀请，先关闭该邀请，再保存新服务地址并生成新链接。客户端粘贴完整链接即可选择同一台服务器。默认服务此前发出的活动邀请可继续使用。自建服务使用该域名的 UDP 3478 做 STUN。

### 用浏览器连接 Windows Host

在 Windows Host 的「连接信息 → 网页访问」点击「开启网页访问」，再将网页链接发给对方。浏览器点击「请求连接」，双方核对六位确认码，Host 点击「允许控制」。网页可以查看主显示器，支持点击、拖动、滚轮、常用按键和中文输入。网页链接可在 10 分钟内发起一次访问，Host 和浏览器都能断开。再次连接需生成新链接。键盘、鼠标和桌面画面通过加密的 WebRTC 数据通道直传。

自建服务使用同一个 HTTPS 站点提供 `/piulink/web` 和 `/piulink/web.js`，并在 `/piulink/web/v1/host` 和 `/piulink/web/v1/connect` 交换限长的 SDP。请使用本目录匹配的 `server` 和 `routes.caddy`。网页邀请的会话密钥放在 URL 的片段中，用于校验协商内容。网页代码由所选服务提供，因此应选择可信服务或自建。两端找不到直连路径时，可在自己的 Tailscale 网络中使用原生客户端。

邀请可重复使用。请只分享给信任的人。服务运营者能看到两端的网络地址，首次配对需要 Host 确认，已配对设备可凭已保存的证书再次连接。关闭邀请会停止本机处理新的打洞请求，并结束由它维护的会话。要移除设备权限，还需在 Host 中移除配对。

安装器备份现有 Caddy 配置、服务文件和二进制，再校验配置并检查 HTTPS；失败时回滚。登记和打洞票据只保存在服务端内存中，重启后由在线 Host 自动重新登记。
