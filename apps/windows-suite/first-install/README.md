# Windows 首次安装包

首次安装包适用于 Windows x64。它用固实压缩保存 Host 和 Client 的文件，在本地恢复公开整合版 EXE，并在启动安装前校验整个文件的 SHA-256。恢复阶段使用 Windows 自带的 .NET Framework 和 Cabinet API。用户无需另外安装 7-Zip、WiX 或下载运行库。恢复需要额外的 CPU 时间和临时磁盘空间。

解出的 EXE 位于当前用户的 `%LOCALAPPDATA%\PiuLink\Installers`。该路径在安装后仍可作为应用内增量更新的种子。交互模式会显示打开原版安装程序的选项。使用 Inno 的静默参数只还原安装包，便于自动核验。

## 构建与验收

从项目现有的 Windows WiX 6.0.0 套件构建环境运行 `Build-FirstInstall.ps1`。传入规范命名的公开整合版 EXE、一个新的工作目录和输出目录。另需 Python、PowerShell 7、7-Zip 和 Inno Setup 6.7.3，均只用于构建。

构建器面向本项目使用独立媒体 CAB 的未签名 Burn 预览布局。它保留原安装器的封装字节和 CAB 目录，将文件按 SHA-256 去重压缩。还原工具在独立目录内重新生成各 CAB，校验每个 CAB 和最终 EXE，校验成功后将结果原子写入用户缓存。

仓库中的 `Publish verified compact Windows installer` 工作流从已发布的 Windows 标签读取整合包。在干净的 Windows 运行器上验收还原、安装及组件校验后，工作流会把首装 EXE 和独立校验表附加到同一 Release。原 EXE 用于常规升级和增量更新。

还原过程使用 WiX Toolset 6.0.0 的原版 `wixnative.exe`。许可随包提供，对应源码在发布附件 `WiX-6.0.0-source.zip` 和 [WiX 6.0.0 源码](https://github.com/wixtoolset/wix/tree/v6.0.0)。
