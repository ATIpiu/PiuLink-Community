# PiuLink 邀请服务

这个 Go 服务保存短期主机地址记录，并协助已配对设备协商直连。HTTP 接口监听 `127.0.0.1:5086`，IPv4 STUN 监听 UDP 3478。音视频由设备直接传输。当前发布版客户端使用内置邀请域名，自行部署其他域名时需要相应配置或构建兼容客户端。

## 构建与运行

从 `services/rendezvous/` 运行：

```sh
go test ./...
go build -trimpath -o server .
./server
```

Linux x86-64 预构建包包含同一源码构建的 `server`、systemd 单元和 Caddy 路由片段。单元的启动路径为 `/usr/local/lib/piulink-rendezvous/server`。使用自己的域名提供 HTTPS，并在对应的 Caddy 站点导入 `routes.caddy`。仓库中的 `install.sh` 是官方部署环境的维护脚本。协议和连接条件见[直连邀请](../../docs/rendezvous.md)。

自有服务代码适用 [PolyForm Noncommercial 1.0.0](../../licenses/PolyForm-Noncommercial-1.0.0.txt)。
