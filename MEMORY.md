# SerialWorkbench 项目记忆

## 当前状态

- 仓库：`G:/TikaLab/SerialWorkbench`
- 分支：`main`
- 代码基线：`5e9fc46 修正终端连接成功后的窗口返回`，记录日期为 2026-10-06。
- 发布目录：`publish/win-x64/`，当前版本为 `1.0.0+a1ec97b`；`5e9fc46` 尚未发布，现有 Host 锁住发布 DLL，退出许可正在等待回复。
- 记忆文件记录生成时的架构、功能和验证边界；当前提交状态以 `git status` 和 `git log` 为准。
- 目标平台：Windows 11；目标运行时为 .NET 10、WinUI 3、self-contained `win-x64`。
- 旧的 `E:/TikaLab/CableTester` 不属于当前项目基线，不能带回旧 WPF 架构、命名或实现。

## 架构

```text
WinUI / TUI / CLI / Agent
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
- Host 共享连接、工作区、命名配置、发送历史与任务，客户端退出保留连接；无客户端、连接和任务后等待 30 秒退出。
- Agent 与 CLI JSON/JSONL 共用 Schema v2；含失败在内的契约记录写 stdout，stderr 仅放诊断。capabilities、schema 和 help 可离线查询。
- CLI 支持连接配置管理、在线控制线与缓冲操作、BREAK、校验追加、循环发送、任务进度和等待。核心业务能力的完整 CLI 覆盖仍需继续完善。

## 终端工作台

- GUI 承载完整人工操作，TUI 聚焦连接、收发、日志与常用配置，CLI 承担完整程序控制与 AI 接口；三者共用 Host RPC。
- TUI 使用 Terminal.Gui 原生控件，不自行绘制终端组件，不添加装饰性 emoji，设备原始文本与字节保持完整。
- 主屏为连接、报文、收发状态和单行发送。工作台、发送历史、任务、会话和设置使用 Alt+1 至 Alt+5 导航。
- F4 连接管理，F8 设置，F9 工具。高级工具包含 Modbus、文件与回环、自动化、协议分析和波形，Esc 返回。
- 80 列及以上使用两栏；60 至 79 列优先报文；最低 60 列、20 行。缩小后恢复保留发送草稿和焦点。
- 底栏按焦点显示常用操作，F1 查看全部按键。报文 Ctrl+F 筛选，Ctrl+C 或右键复制，Enter 详情；发送框 Enter 发送。
- 布局、导航、设置分别位于 `TerminalWorkbench.Layout.cs`、`TerminalWorkbench.Navigation.cs` 和 `TerminalWorkbench.Settings.cs`；业务请求继续通过 `IHostRpc`。
- 连接成功只请求关闭弹窗，通过独立状态恢复输入焦点。`Dialog.Result` 仅表示按钮索引；单个按钮时不能写入 1。成功连接测试同时覆盖 F4 与设置页入口。
- 本机技能：`C:/Users/Tika/.codex/skills/tui-design-pageton/` 与 `C:/Users/Tika/.codex/skills/tui-design-gfargo/`。名称按来源区分，设计参考结合 Terminal.Gui 官方接口使用。

## 验证

- 标准命令：`./eng/test.ps1`。
- 该脚本按 solution 执行 Release 构建和测试。
- 最近 `eng/test.ps1` 结果：0 warnings、0 errors；179 项测试，172 项成功、7 项硬件测试跳过。
- `a1ec97b` 的完整发布通过，CLI、Host、WinUI 文件版本一致，原有 `publish/win-x64/data` 保留。
- TUI 自动化测试覆盖 60x20、80x24、120x40、窗口缩放、焦点、导航、设置输入、连接失败和连接成功返回；修复后未执行实机串口收发。
- 便携发布：`./eng/publish.ps1`，输出目录为 `publish/win-x64/`。
- 发布目录包含 WinUI、Host、CLI 及 PRI/XBF 和 Windows App SDK 文件。
- 实机前必须重新枚举端口：
  `./publish/win-x64/SW_CLI.exe ports list --output json`
- 历史端口包括 COM15、COM18、COM19，历史设备为 USB-SERIAL CH340、VID 1A86、PID 7523。端口号不能视为当前状态。
- COM18/COM19 曾完成双 CH340 TX/RX 交叉通信和 XMODEM-CRC 传输。
- WinUI 已通过 Release/XAML 编译；桌面启动、resize、按钮视觉和当前 COM 实机验收仍需执行。
- 2026-09-02 曾出现发布版启动后无主界面：`data` 已创建，但 `Microsoft.UI.Xaml.dll` 以 `0xc000027b` 退出。应用 `data/logs/crash.log` 给出 `WorkbenchPage.RefreshTrafficFilter()` 第 92 行的空引用，调用来自 XAML 的 `TrafficDirection.SelectedIndex="0"` 早期 `SelectionChanged`。修复提交为 `093c68c`：移除 XAML 默认 SelectedIndex，在 `InitializeComponent()` 完成后由页面构造函数设置。重新发布后窗口标题为 `SERIAL / WORKBENCH`、句柄有效且进程保持运行，crash.log 未新增内容。

## 剩余目标

1. 高速采集：队列深度历史和长时间压力测试。事件流已提供标识、最早和最新序号、连续游标及缺失区间。
2. 设备生命周期：可配置重连间隔/次数和线路状态事件。多连接按设备标识重连、共享连接身份与会话分段已实现。
3. 报文诊断：长度/时间范围筛选、匹配高亮、上一条/下一条、未读数、字节间隔、帧间隔、TX/RX 比例、突发峰值和错误统计。
4. 自动化：条件等待、字段级匹配、变量传递、失败分支、步骤启停、循环、运行日志和每步结果。
5. Modbus：广播地址、寄存器有符号/无符号、32/64 位组合、浮点、字节序/字序、超时率、CRC 错误率和按从站统计。
6. 会话：名称、备注、标签、SQLite integrity_check、WAL 恢复提示、备份恢复、按 TX/RX 回放和固定间隔回放。TUI 已提供倍率回放、暂停与继续。
7. 协议模板：请求—响应关联、字段级错误位置、枚举和位字段解释。
8. 波形：时间轴、采样率、通道名称/单位、游标、触发、峰值/均值/最小/最大值、频率、丢帧、PNG/SVG/CSV 导出。
9. 多连接：统一时间线、更多独立恢复状态和按设备身份绑定任务。
10. CLI：会话事件分页筛选查询与波形数据处理等核心业务入口；不能宣称完整业务能力覆盖已经完成。

## 开发约束

- 每完成一个完整用户功能，运行 `./eng/test.ps1`，按 `docs/code-style.md` 使用简短中文动宾标题建立单目的提交；不自动 push。
- 涉及实机时先重新枚举 COM，不使用历史端口号作为假设。
- 构建、测试、发布必须使用仓库脚本和 solution 流程，不能只构建单个项目。
- 保持 Host/IPC/WinUI/CLI 分层和现有中文文档风格。
- 原始串口数据不能用格式化 HEX 字符串替代。
- 页面通过事件向 MainWindow 请求 Host 操作；不要从页面直接访问串口或存储。
- 已实现能力同步到 `README.md` 和 `docs/feature-gap-analysis.md`；继续清理其中仍保留的历史规划重复项。

## 最近提交

`5e9fc46` 连接成功返回；`a1ec97b` 工具布局；`5d7b238` 连接错误反馈；`347b655` 上下文快捷键；`30d25bc` 窄窗口与状态；`212d11e` 主屏收发；`c9d4b86` 工作区和工具导航；`afa2393` 共享连接参数与编码。
