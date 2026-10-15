# 原厂驱动功能核对

2026-10-15，对比用户提供的 donner-gui.exe DEMO、配套说明/日志，以及当前 Livejack 的 Donner 5.0.0 驱动。功能按当前硬件实际查询结果接入；没有复制 DEMO 程序代码、试用逻辑或修改原厂面板 XML。

## 接入结果

| 功能 | 来源 | 本项目处理 |
|---|---|---|
| 采样率、ASIO 缓冲、输入/输出延迟、通道音量及静音 | 原厂 TUSBAUDIO API | 原有功能保留 |
| 输出 16/24 位格式 | GetSupportedStreamFormats / SetCurrentStreamFormat | 新增选择，使用驱动返回的格式 ID |
| 输入格式 | GetCurrentStreamFormat | 当前仅 24 位双通道，显示状态 |
| ASIO 安全模式 | GetASIOInstanceInfo / SetASIOBufferPreferredSize | 新增开关；修改缓冲保留安全模式 |
| 按需/持续开启音频流 | Get/SetDeviceStreamingMode | 新增选择，交由原厂驱动保存 |
| 时钟和锁定状态 | GetCurrentClockSource | 当前只有内部时钟，显示状态 |
| WDM/ASIO 客户端占用、流运行状态 | GetClientInfo | 新增实时状态；占用时禁用格式和流模式写入 |
| EQ、效果器机架、录音、软件实时链路 | DEMO 日志中的 WASAPI 链路，程序中另有 ASIO 后端字符串 | 软件处理路径，本次不作为原厂驱动功能接入 |
| 解锁 XML 页面、声音设备配置文件、DSP/固件操作 | 配置中可见或 DLL 存在接口名 | 不能据此证明硬件可用，本次不添加控件 |

设置入口：右上角齿轮 → 驱动设置。未成功读取的功能隐藏；格式和流模式需要确认客户端计数为零。启动、重连和刷新不应用这些设置。

## 接口依据

- Format：332 字节；ID @0、位深 @4、通道数 @8、UTF-16 名称 @12。方向 0 输出、1 输入。实机输出 ID65537=24 位、65538=16 位，输入 ID131073=24 位。
- Clock：340 字节；ID @0、锁定 @12、频率 @16、UTF-16 名称 @20。实机 ID41 内部时钟，只有一个可选时钟源。
- ASIO：196 字节；安全模式为 @16 的 `0x10000` 位，DSD 为 @12 的 bit0。缓冲 setter 第四参数为 flags，不能把安全模式当作布尔 1，也不能每次固定传 0。
- ClientInfo：句柄和三个 uint 输出，依次为流状态、WDM 客户端数、ASIO 客户端数。
- StreamMode：1 按需、2 持续开启；setter 第三个参数 bit0 控制保存设置。
- FormatSelectionMode 必须为 1，且非 DSD，才允许格式切换；使用格式 ID 而非序号。
- GetDeviceUsbMode 表示正常音频/DFU 模式，不是 USB 速度。本项目没有据此虚构 USB 速率或把 USB 设备版本当成固件版本。

ABI 和语义来自当前驱动的 API 包装层与内核实现：GetClientInfo 180001b30、GetCurrentClockSource 180001c00、GetCurrentStreamFormat 180001c40、GetSupportedStreamFormats 180001ee0、ASIO 140031ca0/140031e30、格式切换 14002ca30、流模式 140044c20。

## 验证与范围

已在真实声卡上读到 24 位双通道、输出两种格式、内部时钟锁定、WDM/ASIO 客户端计数及按需模式。三个新增写入接口使用现有值调用并回读成功，采样率、缓冲、模式与位深保持原值；实际切换及缓冲保留安全位已通过隔离模拟与核心检查。尚未在真实设备上切换到另一种位深/安全/流模式，也未进行长时间音频稳定性测试。

DEMO SHA-256：c963b09169c3609ebc9ddd3b405f10854bb946b8462998cb3d06db537a28d9bc。REA 确认它为原生 x64 PE（证据 ev_e5875f6ff1941cf19c776f977254474b330e295da0eb8743453a75787a0245bd）。其完整原生分析超时；本报告的接入结论以配套文件、已恢复的原厂接口和实际驱动查询为依据，不代表已验证 DEMO 每一个按钮或所有效果器算法。
