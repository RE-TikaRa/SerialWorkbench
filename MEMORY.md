# SerialWorkbench 项目记忆

## 当前状态

- 仓库：`G:/TikaLab/SerialWorkbench`
- 分支：`main`
- 代码基线：`56123b8 保持终端主题切换的操作状态`，记录日期为 2026-10-07。
- 发布目录：`publish/win-x64/`，四个入口的版本均为 `1.0.0+56123b8e8cf3b2e1349ef8bd8930d19fa7693ddd`，完整发布与命令验证通过，原有 `data/` 保留。
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
- `SerialWorkbench.Tui`：独立的终端启动入口，工作台实现位于 `SerialWorkbench.Cli/Tui`，复用 CLI 的程序集与 RPC 客户端。

构建生成 `SW.exe`、`SW_TUI.exe`、`SW_CLI.exe` 和 `SW_HOST.exe`。GUI 程序集为 `SW`；另外三个项目通过 `AppHostName` 和 `Directory.Build.targets` 指定原生入口，托管程序集保留项目名称。发布脚本合并四个项目产物，必须携带 `SW.pri`、XBF 与运行时资源。

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
- CLI `kill` 按完整可执行路径识别指定应用目录的四个入口，先结束其他客户端，再通过 RPC 取消连接任务、关闭串口与 Host。RPC 不可用或超时则强制结束 Host；结果记录是否强制结束，调用 CLI 输出结果后自行退出。
- `kill` 默认关闭时限为 10000 ms，支持 `--app-root`、JSON/JSONL；没有 Host 时不会启动 Host。失败退出码为 3，结果保留失败进程和 `PROCESS_TERMINATION_FAILED`。

## 终端工作台

- GUI 承载完整人工操作，TUI 聚焦连接、收发、日志与常用配置，CLI 承担完整程序控制与 AI 接口；三者共用 Host RPC。
- TUI 使用 Terminal.Gui 原生控件，不自行绘制终端组件，不添加装饰性 emoji，设备原始文本与字节保持完整。
- `SW_TUI.exe` 无参数启动工作台；`SW_CLI.exe` 无参数显示命令帮助，`--agent` 使用相同 Schema v2。两个入口共用同目录的 `SW_HOST.exe`。
- 主屏为连接、报文、收发状态和单行发送。工作台、发送历史、任务、会话和设置使用 Alt+1 至 Alt+5 导航。
- F4 连接管理，F8 设置，F9 工具。高级工具包含 Modbus、文件与回环、自动化、协议分析和波形，Esc 返回。
- 80 列及以上使用两栏；60 至 79 列优先报文；最低 60 列、20 行。缩小后恢复保留发送草稿和焦点。
- 底栏按焦点显示常用操作，F1 查看全部按键。报文 Ctrl+F 筛选，Ctrl+C 或右键复制，Enter 详情；发送框 Enter 发送。
- 布局、导航、设置分别位于 `TerminalWorkbench.Layout.cs`、`TerminalWorkbench.Navigation.cs` 和 `TerminalWorkbench.Settings.cs`；业务请求继续通过 `IHostRpc`。
- 连接成功只请求关闭弹窗，通过独立状态恢复输入焦点。`Dialog.Result` 仅表示按钮索引；单个按钮时不能写入 1。成功连接测试同时覆盖 F4 与设置页入口。
- 本机技能：`C:/Users/Tika/.codex/skills/tui-design-pageton/` 与 `C:/Users/Tika/.codex/skills/tui-design-gfargo/`。名称按来源区分，设计参考结合 Terminal.Gui 官方接口使用。
- TUI 采用 Terminal.Gui Scheme 语义样式：墨蓝背景、紫色焦点、灰色次要信息、青色 RX、琥珀色 TX；浅色终端按实际背景切换，`NO_COLOR` 使用无色与反色选择。主题变化保留输入草稿、焦点、多选范围和错误状态。
- F9 工具按“设备操作”和“数据分析”分组，工具标题带 `工具 /` 层级；任务页优先显示运行中任务，刷新后按任务 ID 恢复选择；工作台固定显示实时/浏览/暂停/回放模式和所选报文数量，会话底部显示当前筛选摘要。
- 连接、刷新、读取和导出操作超过 150 ms 才显示 ASCII `SpinnerView`；发送、复制、导出成功提示短暂强调，错误持续显示。未知总量任务使用原生活动进度条；设置页提供轻量动效开关。

## 验证

- 标准命令：`./eng/test.ps1`。
- 该脚本按 solution 执行 Release 构建和测试。
- 2026-10-07 的完整发布包含 `eng/test.ps1`：0 warnings、0 errors；211 项测试，204 项成功、0 项失败、7 项硬件测试跳过。默认 `NO_COLOR` 和模拟终端浅/深背景的 TUI 测试均通过。
- 发布版验证 CLI/TUI 帮助、离线 capabilities/schema、JSON/JSONL、空实例 kill、Host 正常关闭、自身退出和重复 kill。验证结束时该发布目录没有残留项目进程。
- CH340 COM20 已验证打开连接、kill 关闭连接与 Host、调用 CLI 退出，以及重新打开端口；未发送数据。该结果不能替代全部硬件测试，端口号需每次重新枚举。
- TUI 自动化测试覆盖 60x20、80x24、120x40、窗口缩放、焦点、多选、导航、工具分组、设置输入、连接失败、主题切换、加载反馈和连接成功返回；不代表当前界面的人工视觉验收。
- ApplicationTests、EntryPointTests 与 KillCommandTests 共用非并行的 `Application processes` 测试集合。它们操作同一目录的客户端与 Host，必须避免 kill 结束其他测试正在使用的进程。
- Windows 进程识别使用有限查询权限与完整映像路径；已退出进程不作为终止失败。枚举与退出之间的状态变化需要通过真实进程句柄核对。
- 便携发布：`./eng/publish.ps1`，输出目录为 `publish/win-x64/`。
- 发布目录包含 GUI、TUI、CLI、Host 四个入口及 PRI/XBF 和 Windows App SDK 文件。
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
6. 会话：名称、备注、标签、SQLite integrity_check、WAL 恢复提示、备份恢复和固定间隔回放。TUI 已提供方向筛选、倍率回放、暂停与继续。
7. 协议模板：帧尾、请求—响应关联、字段级错误位置、枚举和位字段解释。
8. 波形：时间轴、采样率、通道名称/单位、游标、触发、峰值/均值/最小/最大值、频率、丢帧和 PNG/SVG 导出。TUI 已提供 CSV 导出。
9. 多连接：统一时间线和按设备身份绑定任务。
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

`56123b8` 主题切换状态；`ec1f53c` 加载与操作反馈；`3651944` 导航与任务状态；`fb38fa7` 配色与焦点层级；`5a73091` 项目记忆与使用文档；`6612818` 进程退出检查与测试隔离；`6198c8d` 全部连接与进程关闭。
