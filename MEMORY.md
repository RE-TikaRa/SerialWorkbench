# SerialWorkbench 项目记忆

## 当前状态

- 仓库：`E:/TikaLab/SerialWorkbench`
- 分支：`main`
- 代码基线：`093c68c fix: prevent early traffic filter crash`。
- 记忆文件记录生成时的架构、功能和验证边界；当前提交状态以 `git status` 和 `git log` 为准。
- 目标平台：Windows 11；目标运行时为 .NET 10、WinUI 3、self-contained `win-x64`。
- 旧的 `E:/TikaLab/CableTester` 不属于当前项目基线，不能带回旧 WPF 架构、命名或实现。

## 架构

```text
WinUI / CLI
  -> Windows named pipe + StreamJsonRpc
  -> SerialWorkbench.Host
  -> SerialConnectionManager
  -> System.IO.Ports.SerialPort
  -> EventJournal + SessionStore
```

- `SerialWorkbench.Domain`：跨层领域模型。
- `SerialWorkbench.Application`：事件日志和写入租约。
- `SerialWorkbench.Serial.Windows`：串口句柄、读写循环、连接状态和连接计数。
- `SerialWorkbench.Sessions`：每个会话独立的 SQLite `.swbsession` 文件、WAL、事件查询和导出。
- `SerialWorkbench.Host`：连接、租约、会话、工作区和 RPC 的组合运行时。
- `SerialWorkbench.Ipc`：版本化 RPC 合同和命名管道客户端。
- `SerialWorkbench.WinUI`：页面、控件状态和用户交互，不引用串口或存储实现。
- `SerialWorkbench.Cli`：通过 RPC 使用 Host，不复制 Host 业务流程。

串口读取链为：

```text
SerialConnection.ReadLoopAsync
  -> EventJournal
  -> SessionStore
  -> subscriptions
```

读取块不等于协议帧。原始字节先进入事件和会话，再派生为文本、HEX、波形或协议字段。请求/响应操作在 Host 内先建立 RX 订阅、取得写入租约、发送请求、累积分块、处理回显和噪声、校验完整帧、处理异常码和超时。

## 已实现功能

- Windows 串口枚举，显示端口描述、VID、PID 和 `DeviceInstanceId`。
- 波特率、数据位、校验、停止位、流控、编码、DTR、RTS、BREAK、清空 RX/TX。
- 命名连接配置、DUT/DEBUG/CONTROLLER/LOOPBACK 角色、设备身份持久化和按身份重连。
- WinUI 多连接同时打开、切换、关闭；每条连接独立事件历史、解码器、统计和观察者丢弃计数。
- CTS、DSR、DCD 状态；RI 因当前 `System.IO.Ports.SerialPort` API 无公开属性而显示未知。
- RS-485 RTS 半双工方向控制、发送前延时和发送后延时。
- 文本/HEX 发送、编码、CR/LF/CRLF、XOR/SUM8/CRC16-Modbus/CRC16-XModem/CRC32、循环发送和终端输入历史。
- 实时监视全部/RX/TX、文本/HEX/来源筛选；CLI `monitor` 支持 `--direction` 和 `--source`。
- CSV 文本和固定长度二进制波形。
- 通用协议模板：帧头、固定长度、长度字段、字段偏移、U8/I8/U16/I16/U32/I32/F32/Hex、大小端、XOR/SUM8/CRC。
- 自动化序列：固定发送、延时、重复、HEX 响应等待、超时、重试、保存、运行和取消。
- Modbus RTU 01/02/03/04/05/06/0F/10/17；CLI 和 WinUI 均支持事务、从站扫描和周期轮询。
- Modbus WinUI 扫描支持范围、读取地址、超时、间隔、停止和逐从站结果；轮询支持 01/02/03/04、次数、间隔、停止、逐次结果、成功率和平均耗时。
- XMODEM-CRC 文件发送/接收、取消、分块缓存和长度元数据。
- 会话生命周期、原始事件、回环统计、CSV/JSONL/TXT/HEX/原始 binary 导出。
- 会话事件支持连接、方向、来源和 HEX 片段筛选；WinUI 支持筛选和继续加载；CLI 各格式按事件序号分页流式写出。
- 连接快照显示累计 RX/TX 和连接打开以来平均 RX/TX 字节速率。
- Host 状态显示待持久化事件数量和实际写入速率，单位为事件/s。

## 验证

- 标准命令：`./eng/test.ps1`。
- 该脚本按 solution 执行 Release 构建和测试。
- 最近结果：0 warnings、0 errors；43 tests passed、0 failed、0 skipped。
- 便携发布：`./eng/publish.ps1`，输出目录为 `publish/win-x64/`。
- 发布目录必须包含 WinUI、Host、CLI、协议宿主相关文件以及 PRI/XBF 和 Windows App SDK 文件。
- 实机前必须重新枚举端口：
  `./publish/win-x64/serial-workbench.exe ports list --output json`
- 历史端口包括 COM15、COM18、COM19，历史设备为 USB-SERIAL CH340、VID 1A86、PID 7523。端口号不能视为当前状态。
- COM18/COM19 曾完成双 CH340 TX/RX 交叉通信和 XMODEM-CRC 传输。
- WinUI 已通过 Release/XAML 编译；桌面启动、resize、按钮视觉和当前 COM 实机验收仍需执行。
- 2026-09-02 曾出现发布版启动后无主界面：`data` 已创建，但 `Microsoft.UI.Xaml.dll` 以 `0xc000027b` 退出。应用 `data/logs/crash.log` 给出 `WorkbenchPage.RefreshTrafficFilter()` 第 92 行的空引用，调用来自 XAML 的 `TrafficDirection.SelectedIndex="0"` 早期 `SelectionChanged`。修复提交为 `093c68c`：移除 XAML 默认 SelectedIndex，在 `InitializeComponent()` 完成后由页面构造函数设置。重新发布后窗口标题为 `SERIAL / WORKBENCH`、句柄有效且进程保持运行，crash.log 未新增内容。

## 剩余目标

1. 高速采集：最早可用事件序号、丢失序号区间、队列深度历史和长时间压力测试。
2. 设备生命周期：拔出事件、可配置重连间隔/次数、多连接独立恢复、会话分段和线路状态事件。
3. 报文诊断：长度/时间范围筛选、匹配高亮、上一条/下一条、未读数、字节间隔、帧间隔、TX/RX 比例、突发峰值和错误统计。
4. 自动化：条件等待、字段级匹配、变量传递、失败分支、步骤启停、循环、运行日志和每步结果。
5. Modbus：广播地址、寄存器有符号/无符号、32/64 位组合、浮点、字节序/字序、超时率、CRC 错误率和按从站统计。
6. 会话：名称、备注、标签、SQLite integrity_check、WAL 恢复提示、备份恢复、JSON 文件导出、按 TX/RX 回放、倍率和固定间隔回放。
7. 协议模板：请求—响应关联、字段级错误位置、枚举和位字段解释。
8. 波形：时间轴、采样率、通道名称/单位、游标、触发、峰值/均值/最小/最大值、频率、丢帧、PNG/SVG/CSV 导出。
9. 多连接：统一时间线、更多独立恢复状态和按设备身份绑定任务。

## 开发约束

- 每完成一个完整用户功能，运行 `./eng/test.ps1`，按现有英文 Conventional Commits 风格建立单目的提交；不自动 push。
- 涉及实机时先重新枚举 COM，不使用历史端口号作为假设。
- 构建、测试、发布必须使用仓库脚本和 solution 流程，不能只构建单个项目。
- 保持 Host/IPC/WinUI/CLI 分层和现有中文文档风格。
- 原始串口数据不能用格式化 HEX 字符串替代。
- 页面通过事件向 MainWindow 请求 Host 操作；不要从页面直接访问串口或存储。
- 已实现能力同步到 `README.md` 和 `docs/feature-gap-analysis.md`；继续清理其中仍保留的历史规划重复项。

## 最近提交

`093c68c`、`7ce8e08`、`0ab7031`、`433285c`、`f6c50ba`、`70cb992`、`81c832f`、`c53de43`、`cabb371`、`6cd685e`、`6fcd3ad`、`0a22c93`、`954bef3`、`7c8549e`、`8d7abcf`、`bedab0a`、`51ae555`。
