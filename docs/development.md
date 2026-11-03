# 开发与验证

以下命令在仓库根目录运行。普通使用请从 [首页](../README.md)开始。

## 从源码构建

需要 Windows 10/11 x64、[.NET SDK 10.0.401](https://dotnet.microsoft.com/download/dotnet/10.0) 或兼容补丁版本。

```powershell
.\build.ps1
```

输出位于 `dist\DonnerControlPanel`。脚本先运行测试，再发布前端和辅助进程。

生成包含驱动的安装包，需要 [Inno Setup 6.7.3](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3) 和原下载包中的、未经修改且带有效数字签名的 `Donner_UsbAudio_v5.0.0_2021-03-29_setup.exe`：

```powershell
.\build-installer.ps1 -DriverInstaller 'C:\drivers\Donner_UsbAudio_v5.0.0_2021-03-29_setup.exe'
```

脚本验证原厂签名与 SHA-256，输出 `dist\DonnerControlPanel-v1.39-Setup-win-x64.exe`。源码仓库不保存驱动二进制。安装包内驱动为原厂 5.0.0 版本（SHA-256：`12EF3F0F517B2F00A7EBBA3DBC0CAC978E4A71ECE8599B31FFE853304DD147F0`），保持原文件；安装过程交由原厂向导执行。

安装脚本的简体中文语言文件来自 [Inno Setup 仓库](https://github.com/jrsoftware/issrc/blob/main/Files/Languages/ChineseSimplified.isl)，保留作者署名。

单独运行核心测试：

```powershell
dotnet run --project tests/Donner.Tests -c Release
```

界面冒烟检查：

```powershell
.\dist\DonnerControlPanel\DonnerControlPanel.exe --smoke build\smoke
```

冒烟检查采用独立数据目录；真实设备仅查询，设置操作在内部模拟数据中验证。生成页面图片与 `smoke-result.json` 后退出。

## 项目结构

| 目录 | 用途 |
|---|---|
| `src/Donner.Core` | 能力检查、音量映射、预设、设备状态与进程通信 |
| `src/Donner.Host` | 隔离进程调用原厂控制 API，检测设备并读写设置 |
| `src/Donner.Panel` | WPF 中文界面、托盘、自动启动与面板说明 |
| `tests/Donner.Tests` | 80 项核心回归检查，无第三方测试框架 |

源码仓库与便携 ZIP 不包含原厂驱动；统一安装 EXE 包含未经修改的原厂驱动安装程序。品牌名称用于说明兼容设备；此项目非 Donner 官方产品。

驱动功能和实时电平说明见 [功能说明](driver-features.md)。


## 版本验证记录

- [v1.38：配置保存、完整预设、十段输入 EQ 与输出频谱](verification/v1.38-persistence-and-eq.json)
- [v1.39：重启引擎、预设布局与滤波器检查](verification/v1.39-ui-and-filter-checks.json)
