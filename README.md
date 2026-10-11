# Donner Livejack 中文控制面板

适用于 Donner Livejack 的非官方 Windows 桌面控制面板。使用原厂驱动与控制 API，提供紧凑的中文界面，不替换声卡驱动。

![设备控制界面](docs/images/main.png)

## 下载与运行

在 [Releases](https://github.com/topsai/donner-livejack-control-panel/releases/latest) 下载 Windows x64 压缩包，解压后双击 `DonnerControlPanel.exe`。请保留整个目录。程序自带 .NET 运行时，无需另装 .NET。

需要先安装 Donner 原厂驱动并连接声卡；程序会自动寻找驱动并检测设备。驱动可从 [Donner 官方下载页](https://us.donnermusic.com/pages/download) 获取。

当前验证的设备为 USB `VID_152A / PID_885C`，系统名称为 `Livejack Audio Interface`。当前支持的控制 API 为 5.0.0.0 / ABI 5.7，程序会校验 DLL 哈希；其他型号或版本暂未验证。

## 功能

- 采样率与 ASIO 缓冲调节，显示驱动报告的输入、输出延迟和设备名称。
- 两路输入、左右输出音量及静音，左右输出联动，全输出静音/取消静音切换。
- 拖动滑块、直接输入百分比或用方向键按 1% 选档；实际音量受硬件步进限制，相邻档位可能对应相同音量。
- USB 拔插后自动重连，丢弃旧设备状态并重新读取当前设置。
- 预设保存与应用、诊断日志、最小化至托盘、可关闭的 Windows 登录自启动。
- 面板说明：点击照片中的 01–13 编号，查看对应接口、旋钮与开关的中文说明。

默认窗口为 900 × 620。音频设置与通道音量左右排列；其他页面从右上角齿轮菜单进入。

启动和重连只读取设置，不自动应用预设。没有获得设备支持或未成功读取的功能不会显示。当前不包含 EQ、降噪、回环、固件升级或硬件按钮远程控制。

## 使用细节

- F5：重新检测；Ctrl+S：保存预设；Ctrl+M：切换输出静音。
- 点击关闭按钮退出；最小化后双击托盘图标恢复。
- 配置与日志位于 `%LOCALAPPDATA%\DonnerControlPanel`。
- 开机启动仅对当前 Windows 用户生效，登录后在托盘运行。移动程序目录后，打开程序可更新已启用的启动项路径。
- 应用预设会校验设备与能力；写入后读取设备实际返回状态。调节音量不会自动取消静音。

## 从源码构建

需要 Windows 10/11 x64、[.NET SDK 10.0.401](https://dotnet.microsoft.com/download/dotnet/10.0) 或兼容补丁版本。

```powershell
.\build.ps1
```

输出位于 `dist\DonnerControlPanel`。脚本先运行测试，再发布前端和辅助进程。

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
| `tests/Donner.Tests` | 52 项核心回归检查，无第三方测试框架 |

本仓库和下载包不包含 Donner 原厂安装包、驱动 DLL、SYS 或 INF。品牌名称用于说明兼容设备；此项目非 Donner 官方产品。
