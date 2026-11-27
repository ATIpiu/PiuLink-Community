# 增量更新

更新入口会按平台、架构和安装方式查找比当前版本更新的 GitHub Release。Windows Host 和 Client 识别系统已安装的整合版。Android 在设置中提供“检查更新”，macOS 和 Linux 客户端在主窗口中提供此按钮。鸿蒙原生客户端在「设置 → 关于 → 检查更新」下载并保存 HAP。Linux Host 可运行 `piulink-host update check` 或 `piulink-host update download [旧安装包路径]`。

Android 直接复用本机已安装的正式 APK。Windows、macOS 和 Linux 会先找“下载”目录内对应版本的官方原包或上次已校验的更新缓存。iOS 可以从“文件”选择旧的官方未签名 IPA；本机重新签过名的文件可能减少可复用的内容。鸿蒙优先使用上次已校验的 HAP 或本机可读的安装包，也能在文件选择器中指定旧包。Linux DEB 还会检查 APT 归档缓存。缺少旧包时首次可能下载完整文件。精简版、通用版和不同架构分别更新。

客户端验证内容索引和完整安装包的 SHA-256，只有校验成功才交给用户。Windows 和 Android 使用系统安装程序。macOS 与 Linux 客户端打开校验后的包所在目录；Linux Host 输出文件路径。鸿蒙保存校验后的未签名 HAP，真机侧载需使用原签名。iOS 通过分享面板导出未签名 IPA，需要使用自己的签名和侧载工具。macOS 和 iOS 未做苹果真机验证。

## 发布

将正式命名的安装包上传至 GitHub Release，然后发布。默认分支的 release-catalog 工作流读取 GitHub 提供的资产 SHA-256，生成分块索引，按各渠道独立推进版本。已有发布若后补资产，可从 Actions 手动运行并指定原 Tag。可以用 `python3 tools/update-release-catalog.py TAG` 本地复核。发布后保持同一版本的文件内容固定。

`updates/channels.json` 记录 17 个渠道的目标文件及哈希。索引用散列命名，保证正在下载的客户端仍能取得原索引。更新器从公开的 GitHub Release 文件请求缺失的字节区间，网络不支持分段时可下载完整包。升级首次具有此入口的旧版本，需要从 GitHub 下载安装一次。

Linux 发布时可运行 `python3 tools/reuse-tar-metadata.py 旧归档 新归档 --level 5` 沿用未变化文件的时间戳。被控端沿用压缩级别 9。在两个标准压缩布局之间首次迁移，实际下载量以客户端显示为准。
