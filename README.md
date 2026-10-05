# SerialWorkbench

SerialWorkbench 是面向 Windows 11 的串口调试工作台。它把串口连接、原始报文、协议解析、会话记录和自动化操作放在同一条调用链中，适合单片机、USB 转串口、RS-232、RS-485 和 Modbus RTU 设备调试。

## 能做什么

- 枚举 Windows 串口，并显示端口描述、VID、PID 和设备实例标识。
- 配置端口、波特率、数据位、校验、停止位、流控、DTR 和 RTS。
- 保存、重命名、删除和应用命名串口连接配置。
- 为连接配置 `DUT`、`DEBUG`、`CONTROLLER` 或 `LOOPBACK` 逻辑角色。
- Host 按 `DeviceInstanceId` 自动恢复断开的设备，保持共享连接标识，并为每次打开记录独立分段。
- 在连接管理页同时打开、切换和关闭多条串口连接，并分别查看角色、设备身份、收发计数和事件历史；这里打开的连接使用 8N1、无流控，编码沿用工作台设置。
- 在连接管理页查看 DTR、RTS、CTS、DSR 和 DCD 状态，RI 在当前串口 API 下显示为未知。
- 在连接管理页查看 Host 最新事件序号和待持久化事件数量。
- 在连接管理页查看会话事件实际写入速率（事件/s）。
- 查看每条连接的观察者队列丢弃块数，区分界面观察与原始串口接收。
- 在连接管理页查看连接打开以来的平均 RX/TX 字节速率。
- 为 RS-485 半双工发送配置 RTS 方向控制，以及发送前、发送后延时。
- 发送文本或 HEX 数据，设置文本编码、CR、LF、CRLF 和校验追加。
- 按固定间隔循环发送，并在同一报文流中查看 TX 和 RX。
- 使用文本或 HEX 监视报文，查看时间戳、完整原始字节和实时计数。
- 将接收数据解析为 CSV 文本或固定长度二进制波形。
- 使用交互式串口终端发送数据、浏览输入历史和复制响应。
- 构造并执行 Modbus RTU 读线圈、读离散输入、读寄存器、读取设备标识、写单个/多个线圈和写单个/多个寄存器事务。
- 区分 TX 回显、无关 RX、合法响应、CRC 错误、异常响应和超时。
- 使用协议帧查看器检查地址、功能码、长度、CRC 和异常码。
- 按固定、递增或随机数据执行 RX-TX 回环检测，保存差异位置、耗时和吞吐率。
- 编辑、保存、运行和取消多步骤自动化发送序列。
- 使用 XMODEM-CRC 发送和接收文件，显示块数、重试次数、速度和错误。
- 保存独立的 SQLite 会话文件，查看完整事件、导出 CSV 和按原始时间间隔回放。
- 通过连接管理页同时打开、切换和关闭多条串口连接，并分别保留事件历史。
- 通过 CLI 执行端口枚举、工作区管理、会话查询与导出、发送、监视、回环、Modbus 和 XMODEM 操作。

所有功能共享 Host 的串口连接和写入租约。原始字节先写入事件和会话，再派生为文本、HEX、波形或协议结果。

## 终端工作台

在 Windows Terminal 中运行 `serial-workbench.exe` 进入 Terminal.Gui 工作台。连接列表、报文表格、输入区、状态栏和标签页使用库控件，支持鼠标、键盘焦点、滚动和窗口尺寸变化。

设置页选择端口、波特率、数据位、校验、停止位、流控、设备角色、编码、HEX 分行间隔和行尾，也可设置 DTR/RTS、RS-485 方向延时与自动重连。打开连接后切回工作台。输入区使用 Enter 发送，发送历史可选择后重新载入。报文支持方向和内容过滤，文本与 HEX 使用和 WinUI 相同的跨读取块处理。

F2 暂停显示，F3 清空，F4 设置，F5 刷新端口，F6 复制所选报文，F7 返回实时视图，Ctrl+Q 退出。表格支持范围选择；选择报文时停止跟随，刷新和筛选保留仍可见的选择。退出终端工作台保留 Host 共享连接，并明确取消前台任务。

发送区独立选择文本或 HEX、行尾和 XOR／SUM8／CRC16 Modbus／CRC16 XMODEM／CRC32 校验追加。循环发送由 Host 持有写入租约，按间隔和次数执行；次数 0 持续运行，使用发送区停止按钮或任务页取消。
任务进度保留最近 10000 次更新，最终结果独立保存。

自动化页提供步骤表格和参数编辑，支持新增、更新、删除、上移、下移步骤，加载和保存序列 JSON。步骤包含原始数据、重复次数、间隔、发送后等待，以及响应 HEX、超时和重试次数。运行时由 Host 执行，任务页提供取消与结果。

连接列表支持关闭、重连和详情查看，详情包含设备身份、分段、统计与线路状态。设置页可在线更新 DTR/RTS、清空 RX/TX 缓冲和发送 BREAK。报文可按来源筛选，Ctrl+C 或右键菜单复制所选内容，Enter 查看完整报文；详情和任务结果可复制全文。

F1 查看快捷键帮助。设置页保留端口、波特率、编码和 HEX 间隔；串口参数、控制线、配置与工作区使用独立对话框。会话筛选、导出格式和回放倍率集中在“筛选与回放”对话框，报文复制格式使用紧凑选择器。

Modbus 页支持位、寄存器事务、从站扫描和周期轮询。文件与回环页提供 XMODEM 文件收发及回环检测，自动化页编辑和运行序列。任务页显示 Host 任务、进度和状态，支持取消任务、查看结果。默认在界面中跟踪任务，正常退出时明确取消；勾选顶部“后台任务”后，任务在退出界面后继续执行，XMODEM 接收仍由 Host 保存文件。

回放使用独立缓冲，实机采集继续运行。停止或完成回放后保留回放内容供筛选和复制，F7 返回实时数据。会话读取和回放均按分页执行，切换会话后忽略上一查询返回的内容。

会话页按连接 ID、RX/TX、来源和 HEX 片段筛选，导出支持 CSV、JSONL、文本日志、HEX 和原始二进制。导出与回放采用当前筛选；回放支持倍率、暂停和继续。会话还可查看回环记录、事件详情，或删除已结束的会话。

Modbus 页实时呈现每次响应的从站、功能码、数值、耗时和状态，支持查看完整响应、复制结果和停止任务。波形页支持暂停、清空、自动缩放与手动浏览、CSV 导出；切换连接或重连分段时重置解析状态。

会话页通过 Host 分页读取原始事件，支持 HEX 筛选、CSV 导出、按原始时间回放和停止回放。协议分析页检查 Modbus 帧，也可选择通用协议模板显示字段和校验结果。波形页使用 Terminal.Gui 的 GraphView、ScatterSeries 和 PathAnnotation 显示 CSV 或二进制采样；图形控件提供滚动和绘制。

## 运行结构

发布目录中有三个入口：

```text
SerialWorkbench.exe       WinUI 3 桌面程序
serial-workbench.exe      终端工作台；带命令时运行 CLI
SerialWorkbench.Host.exe  串口和会话服务
```

桌面程序和 CLI 通过 Windows 命名管道连接 Host。一个应用目录对应一个 Host 实例，连接由 Host 持有。相同参数打开同一串口时复用 `connectionId`；关闭桌面窗口保留共享连接。发送、回环、Modbus、自动化和 XMODEM 操作通过连接写入租约协调，连接占用时新的写入立即失败。

`connections open --port COM16` 创建持久连接，`connections list` 查询连接标识，`connections close --id CONNECTION_ID` 明确关闭。发送、监视、Modbus 和 XMODEM 命令可通过 `--connection CONNECTION_ID` 使用现有连接，命令结束后保留连接。使用 `--port` 的一次性命令创建并关闭自己的临时连接。

Host 在存在客户端、连接或写入任务时保持运行；全部结束后等待 30 秒退出。

工作区选择由 Host 保存，WinUI、TUI 和 CLI 共用同一个工作区及活动会话，Host 重启后恢复选择。TUI 设置页可选择工作区或返回全局数据目录；工作区切换前需要关闭连接。

命名连接配置和最近 20 条发送历史由 Host 保存并共享。WinUI 与 TUI 可应用、创建、修改或删除配置，按设备实例标识寻找当前串口。`profiles list` 与 `history list` 提供相同数据的 CLI 和 Agent 查询入口。

设备断开后由 Host 每两秒检查恢复条件，WinUI、TUI 和 Agent 共用同一个 `connectionId`。重连更换 `segmentId`，原始事件和会话保留分段，文本与 HEX 不跨分段拼接。周期读取等待设备恢复；写入、自动化和文件传输停止，不自动重放。`connections reconnect --id CONNECTION_ID` 明确重连，`connections open --no-reconnect` 关闭自动恢复。

## Host 任务

发送、回环、自动化序列、Modbus 事务、扫描、轮询和 XMODEM 由 Host 执行。每项任务具有 `operationId`，提供进度、结果和取消操作。客户端意外断开保留已接受的任务；前台操作使用 Ctrl+C 或正常退出时发送明确取消。

```powershell
serial-workbench operations list
serial-workbench operations show --id OPERATION_ID
serial-workbench operations cancel --id OPERATION_ID
serial-workbench operations start --connection CONNECTION_ID --kind modbus.poll --parameters '{"slaveAddress":1,"address":0,"quantity":1,"count":10}' --request-id READ_001
```

`operations start` 返回后台任务标识，`operations result` 查询结果。`--request-id` 保存请求身份，相同身份和参数返回原任务；参数不同时返回 `REQUEST_ID_CONFLICT`。任务和结果保存于 `data/operations.sqlite3`。

发送、Modbus、回环、序列和 XMODEM 命令支持 `--background` 与 `--request-id`。默认等待结果；后台模式返回 `operationId`，可通过任务命令查询或取消。使用 `--port` 与这两个选项时，连接由 Host 打开并保留，任务结束后可使用 `connections close` 关闭。相同端口请求在 Host 重启后也返回保存的结果，不再次打开串口或发送。XMODEM 接收文件由 Host 写入指定路径，后台接收同样保存文件。

```powershell
serial-workbench --agent send --port COM16 --text "测试" --background --request-id SEND_001
serial-workbench --agent modbus poll --connection CONNECTION_ID --count 100 --background
```

连接被占用时返回 `CONNECTION_BUSY`，包含占用任务标识。Host 重启后未完成任务标为 `Interrupted`，执行结果为 `Unknown`，设备操作不自动重放。

实时事件包含流标识和递增游标。监视从当前最新事件开始，通过长轮询接收新增数据；过滤未命中的事件同样推进游标。保留区间被截断时返回缺失序号范围，Host 事件流变化时要求重置游标。

## 获取可运行版本

运行 `eng/publish.ps1` 后，完整的 self-contained 便携版位于：

```text
publish/win-x64/
```

从该目录运行：

```powershell
.\publish\win-x64\SerialWorkbench.exe
```

便携版不需要安装 .NET 运行时。目录必须具有写入权限，因为 Host 会在程序目录旁创建 `data/`。

## 桌面页面

### 工作台

工作台是日常串口调试入口，包含：

- 端口、波特率和显示格式；连接配置、串口参数、控制线和 RS-485 设置位于“高级参数”弹出面板。
- 命名连接配置。
- 连接逻辑角色。
- 文本或 HEX 报文监视。
- 时间戳、暂停显示、清空和复制 HEX；切换时间戳或文本/HEX 显示会同步更新已有报文。
- CSV 文本或固定二进制帧波形。
- 文本或 HEX 发送，`Ctrl+Enter` 发送当前输入。
- CR、LF、CRLF 行尾。
- XOR、SUM8、CRC16-Modbus、CRC16-XModem 和 CRC32 校验追加。
- 定时循环发送。
- 连接后在线切换 DTR/RTS、清空 RX/TX 缓冲和发送 100 ms BREAK。RTS 流控或 RS-485 方向控制启用时，RTS 不能手动切换。

暂停只影响工作台显示，串口事件仍由 Host 接收并保存。终端、Modbus、回环和自动化页面复用当前连接。

文本监视跨串口读取块连续显示，按 CR、LF 或 CRLF 分行。未收到行尾的内容实时追加到当前行；切换连接、方向或来源时开始新的显示段。HEX 模式将同一连接、同一来源的连续 RX 数据按接收间隔合并显示，默认 10 ms，TX 每次发送独立显示。“高级参数”中的“HEX 接收分行间隔”可调整间隔，设为 0 时按原始读取块显示。实时接收和回放都使用原始事件时间进行聚合，复制使用聚合后的完整字节；会话保存原始事件。

工作台和终端报文列表支持单击选择、Ctrl 多选和 Shift 范围选择。右键可以复制所选条目的当前显示内容、全选或清除选择，Ctrl+C 复制所选内容。复制按列表顺序排列；选中条目时停止自动滚动，刷新、暂停和历史截取会保留仍可见条目的选择。

右键“复制为”提供文本、空格分隔 HEX、连续 HEX，以及含时间、RX/TX 方向和来源的日志。文本使用报文对应的编码解码所选原始字节，保留原始行尾，支持跨所选读取块的多字节字符。连续 HEX 不包含空格和换行。工具栏复制按钮复制当前列表的 HEX。

### 终端

终端提供独立的交互式输入区，但不创建第二条串口通道。支持：

- 文本和 HEX 输入，`Ctrl+Enter` 发送。
- 当前串口编码。
- 无行尾、CR、LF、CRLF。
- 输入历史。
- 清空输入。
- 复制列表 HEX，或右键按指定格式复制所选报文。
- 清空终端显示。

终端 TX 会进入普通事件记录，接收数据同时出现在终端和工作台报文流中。

### Modbus RTU

页面可以构造并发送：

- 功能码 03：读保持寄存器。
- 功能码 04：读输入寄存器。
- 功能码 06：写单个保持寄存器。
- 功能码 01、02：读线圈和读离散输入。
- 功能码 05、0F、10：写单个或多个线圈、寄存器。
- 功能码 17：读取设备标识。

从站扫描区域按地址范围使用功能码 03 读取一个保持寄存器，逐个显示正常响应和异常响应，支持设置超时、间隔和停止扫描。

周期轮询区域支持功能码 01、02、03、04，按采样次数和固定间隔记录每次响应，显示位或寄存器结果、耗时和失败信息，并汇总成功率与平均响应耗时，可随时停止。

事务在 Host 内完成 RX 订阅、发送、分块接收、帧长判断、CRC 校验、异常码解析和超时处理。收到与请求完全相同的帧时，会标记为 TX 回显并继续等待从站响应。

响应区保留实际收到的 HEX。没有真实从站时，RX-TX 回接只能验证回显识别，不能产生合法 Modbus 响应。

### 协议帧

协议帧查看器提供 Modbus RTU 模板，可以解析：

- 地址。
- 功能码。
- 请求或响应类型。
- 数据长度。
- 数据地址和寄存器值。
- CRC 计算值与实际值。
- 异常码。
- 帧长和解析失败原因。

解析结果是原始帧的派生视图，不会替换原始 HEX。

### 自动化

自动化页面以 JSON 编辑发送序列。每一步包含：

```json
{
  "data": "01 03 00 00 00 01 84 0A",
  "format": "hex",
  "delayMilliseconds": 100,
  "repeatCount": 2,
  "waitMilliseconds": 0,
  "responseHex": "01 06 00 10 00 01",
  "responseTimeoutMilliseconds": 2000,
  "retryCount": 2
}
```

序列还包含名称和步骤列表，保存时名称作为工作区 `sequences` 目录中的文件名。设置 `responseHex` 后，Host 会在每次发送前订阅 RX，跨读取块匹配响应；超时按 `retryCount` 重发，成功后再执行下一步。运行期间所有 TX 进入普通会话事件，取消后不会发送后续步骤。

### 文件传输

文件传输使用 XMODEM-CRC，支持发送文件和接收文件。传输结果显示块数、速度、重试次数和错误信息，并通过写入租约与普通发送互斥。

XMODEM 需要另一端设备或模拟器完成 `NAK/C`、数据块确认和 `EOT` 协商。RX-TX 回接不能模拟完整的 XMODEM 对端。

### 回环检测

回环检测向当前串口发送固定、递增或随机数据，并比较收到的原始字节。结果包含：

- 成功或失败。
- 完成的迭代次数。
- 发送和接收字节数。
- 首个差异位置。
- 期望字节和实际字节。
- 持续时间。
- 吞吐率。

结果保存在当前会话的 `loopback_results` 表中，不伪装成串口报文事件。

### 会话记录

会话页支持：

- 按会话时间、状态和统计摘要筛选会话。
- 查看完整原始事件，并按方向、来源和 HEX 片段筛选。
- 大型会话按序号继续加载后续事件。
- 查看回环统计。
- 导出完整 CSV。
- 按原始事件时间间隔回放。
- 暂停、继续和停止回放。
- 在资源管理器中定位会话文件。
- 删除已经结束的会话。

回放只写入工作台和终端的界面事件流，不打开串口，不产生 TX，也不会混入原会话或当前连接的报文历史。回放结束或停止后，工作台和终端恢复显示当前连接的报文。

回放与实时接收使用相同的文本分行规则，支持跨原始事件的字符解码和 CRLF 分行。多连接会话分别保留解码状态，按原始事件顺序显示。

## 后续增强

### 多连接与设备生命周期

- 在 WinUI 中同时管理多条连接，每条连接拥有独立报文流、发送队列、筛选器和统计。
- 使用 `DUT`、`DEBUG`、`CONTROLLER`、`LOOPBACK` 等逻辑角色组织设备和任务。
- 将连接配置与 `DeviceInstanceId` 绑定，支持设备拔出、插回识别和自动重连。
- 为当前连接执行设备拔出后的自动重连，并显示重连状态。
- 配置重连间隔、重连次数、多连接独立恢复、统一时间线、会话分段和状态提示。
- 显示 CTS、DSR、DCD、RI 等串口控制线状态。

### 响应驱动的协议与自动化

- 支持帧头、帧尾、长度字段、字段偏移、大小端和校验范围配置。
- 支持整数、浮点、枚举和位字段解析，并提供协议模板导入导出。
- 为自动化序列增加响应匹配、超时、重试、条件分支、失败分支和步骤结果。
- 在步骤之间传递变量和解析字段，记录每一步的 TX、RX、耗时和判断结果。
- 扩展 Modbus 功能码 `01`、`02`、`05`、`0F`、`10`、`17`，增加广播、周期轮询、从站扫描和寄存器类型解释。

### 高速采集与诊断

- 显示接收速率、写入速率、持久化队列深度、观察者丢弃计数和丢失序号区间。
- 支持高速接收压力测试，以及原始二进制、CSV、JSONL 流式导出。
- 按长度、时间范围和十六进制内容搜索报文，支持匹配高亮、上一条、下一条和标记。
- 统计字节间隔、帧间隔、吞吐率、突发峰值、TX/RX 比例和错误率。
- 支持采集触发、只回放 TX、只回放 RX、回放倍率和固定间隔回放。
- 提供会话之间的报文差异比较，以及会话名称、标签、备注、完整性检查、备份和恢复。

### 串口与文件传输扩展

- 增加清除串口错误状态、RS-485 半双工方向控制和 RTS 前后置延时。
- 支持 YMODEM、ZMODEM 和自定义文件传输协议。
- 提供自定义 BREAK 时序和更细的传输诊断信息。

实施顺序为：多连接与设备恢复，响应驱动自动化与协议模板，高速采集与会话分析，Modbus 扫描轮询与文件传输扩展。详细边界见 [功能边界与后续增强](docs/feature-gap-analysis.md)。

## 数据目录

应用根目录是入口程序所在目录。

```text
data/
├─ settings/
│  ├─ serial-preferences.json   最近使用的界面和串口参数
│  ├─ serial-profiles.json      命名串口连接配置
│  └─ workspace.json            当前工作区路径
└─ sessions/
   └─ *.swbsession              SQLite 会话文件
```

未选择工作区时，会话位于应用目录的 `data/sessions/`。选择工作区后，新会话写入工作区的 `sessions/`，应用级设置仍保存在应用目录的 `data/settings/`。

每个 `.swbsession` 文件独立保存：

- UTC 时间。
- 单调时钟值。
- 递增序号。
- 连接标识。
- RX/TX 方向。
- 来源。
- 原始字节。
- 可选消息。
- 回环检测统计。

CSV 导出字段为：

```text
utc,direction,source,hex,byte_count
```

导出内容直接来自 SQLite 的 `events` 表，不使用界面列表中的截断文本。

## CLI

命令、参数类型、Help、补全建议和 Response File 由 System.CommandLine 提供。各命令使用 `--help` 查看参数，`serial-workbench @commands.rsp` 从文件读取参数；布尔开关可与带空格的文本参数同时使用。文本结果使用 Spectre.Console 的表格和面板呈现，协议检查无需启动 Host。

在 `publish/win-x64/` 目录执行 `serial-workbench.exe`。CLI 会自动启动同目录的 Host。

### 端口和 Host

```powershell
.\serial-workbench.exe ports list --output json
.\serial-workbench.exe host status --output json
.\serial-workbench.exe host stop
```

### 工作区

```powershell
.\serial-workbench.exe workspace show --output json
.\serial-workbench.exe workspace set --path E:\Workspaces\DeviceA --output json
.\serial-workbench.exe workspace clear --output json
```

切换工作区前必须关闭所有串口连接。

### 发送

HEX 发送：

```powershell
.\serial-workbench.exe send --port COM15 --baud 115200 --hex "55 AA 01 02" --output json
```

文本发送：

```powershell
.\serial-workbench.exe send --port COM15 --baud 115200 --text "设备状态" --encoding utf-8 --line-ending crlf --output json
```

串口参数还可以使用：

```text
--data-bits 5|6|7|8
--parity None|Odd|Even|Mark|Space
--stop-bits One|OnePointFive|Two
--handshake None|XOnXOff|RequestToSend|RequestToSendXOnXOff
--role Dut|Debug|Controller|Loopback
--device-id DEVICE_INSTANCE_ID
--rs485
--rts-before MILLISECONDS
--rts-after MILLISECONDS
--dtr
--rts
```

### 监视

```powershell
.\serial-workbench.exe monitor --port COM15 --baud 115200 --seconds 10 --output jsonl
```

只看接收或发送事件：

```powershell
.\serial-workbench.exe monitor --port COM15 --baud 115200 --direction rx --source serial --seconds 10 --output jsonl
```

`--direction` 可使用 `all`、`rx` 或 `tx`，`--source` 按来源名称不区分大小写匹配。文本输出显示 UTC 时间、方向和 HEX；`jsonl` 每行输出一个带 `schemaVersion` 的结构化事件。

### 回环

```powershell
.\serial-workbench.exe loopback run --port COM15 --baud 115200 --pattern Incrementing --length 4096 --iterations 4 --timeout 5000 --output json
```

可选模式为 `Fixed`、`Incrementing` 和 `Random`。还可以使用 `--seed` 固定随机数据种子。

### Modbus

读线圈、离散输入、保持寄存器或输入寄存器：

```powershell
.\serial-workbench.exe modbus read --port COM15 --baud 115200 --slave 1 --address 0 --quantity 1 --function 3 --timeout 2000 --output json
```

写单个寄存器：

```powershell
.\serial-workbench.exe modbus write --port COM15 --baud 115200 --slave 1 --address 0 --value 1 --timeout 2000 --output json
```

写单个线圈：

```powershell
.\serial-workbench.exe modbus write --port COM15 --baud 115200 --slave 1 --address 0 --value 1 --function 5 --timeout 2000 --output json
```

写多个线圈或寄存器：

```powershell
.\serial-workbench.exe modbus write --port COM15 --baud 115200 --slave 1 --address 0 --values "1,0,1,1" --function 15 --output json
.\serial-workbench.exe modbus write --port COM15 --baud 115200 --slave 1 --address 0 --values "10,0x0102" --function 16 --output json
```

功能码可以使用 `1`、`2`、`3` 或 `4`。Modbus JSON 结果包含请求帧、响应帧、功能码、位数组、寄存器、地址、寄存器值、异常码、耗时和错误信息。

扫描从站：

```powershell
.\serial-workbench.exe modbus scan --port COM15 --baud 115200 --from 1 --to 247 --address 0 --timeout 200 --output json
```

扫描使用功能码 `03` 读取一个保持寄存器。正常响应和 Modbus 异常响应都会列入结果，超时从站不会列入响应列表。

周期轮询：

```powershell
.\serial-workbench.exe modbus poll --port COM15 --baud 115200 --slave 1 --address 0 --quantity 4 --function 3 --count 20 --interval 1000 --output jsonl
```

每次轮询都是独立的 Host Modbus 事务，输出包含采样序号、UTC 时间、原始响应、寄存器或位数组、耗时和错误信息。

### 协议解析

使用与 WinUI 协议帧查看器相同的 Modbus RTU 解析器：

```powershell
.\serial-workbench.exe protocol inspect --hex "01 03 02 00 0A 38 43" --output json
```

命令会返回地址、功能码、帧长、CRC、异常码和解析失败原因。解析失败时退出码为 `1`，原始 HEX 不会被修改。

通用协议模板使用 JSON 文件描述帧头、长度字段、字段和校验：

```powershell
.\serial-workbench.exe protocol inspect --template E:\Protocols\sensor.json --hex "AA 01 34 12 00 00 80 3F 5B" --output json
```

模板字段类型支持 `U8`、`I8`、`U16`、`I16`、`U32`、`I32`、`F32` 和 `Hex`，数值字段支持 `LittleEndian` 与 `BigEndian`。

### 会话

列出会话：

```powershell
.\serial-workbench.exe sessions list --output json
```

查看会话的完整事件和回环统计：

```powershell
.\serial-workbench.exe sessions show --id SESSION_ID --output json
```

导出会话 CSV：

```powershell
.\serial-workbench.exe sessions export --id SESSION_ID --file E:\Exports\session.csv --output json
```

导出流式 JSONL：

```powershell
.\serial-workbench.exe sessions export --id SESSION_ID --file E:\Exports\session.jsonl --format jsonl --output json
```

按方向、来源或 HEX 片段筛选后导出：

```powershell
.\serial-workbench.exe sessions export --id SESSION_ID --file E:\Exports\rx.jsonl --format jsonl --direction rx --source serial --hex "01 03" --output json
```

`--direction` 可使用 `all`、`rx` 或 `tx`；`--source` 不区分大小写匹配来源；`--hex` 匹配报文中的连续字节。CSV 和 `jsonl` 都按会话事件序号分页写入，适合大型会话处理。

导出筛选后的原始二进制：

```powershell
.\serial-workbench.exe sessions export --id SESSION_ID --file E:\Exports\payload.bin --format binary --direction rx --output json
```

`binary` 按事件序号拼接原始报文字节，不添加时间戳或分隔符。

`text` 每行包含 UTC 时间、方向、来源和 HEX；`hex` 每行只包含一个事件的 HEX 数据。

删除已经结束的会话：

```powershell
.\serial-workbench.exe sessions delete --id SESSION_ID --output json
```

### XMODEM

发送文件和接收文件需要两个独立串口端点。两端应使用相同的串口参数，并交叉连接 TX、RX 和 GND：

```powershell
# 终端 1：先启动发送端，使其等待接收端的 C
.\serial-workbench.exe xmodem send --port COM19 --baud 115200 --file E:\Transfers\payload.bin --output json

# 终端 2：再启动接收端
.\serial-workbench.exe xmodem receive --port COM18 --baud 115200 --file E:\Transfers\received.bin --output json
```

测试时先启动发送任务，再启动接收任务，使发送端先进入等待 `C` 的状态。XMODEM 发送和接收使用独占连接写入租约，不会与同一连接上的普通发送并行执行。

## CLI 输出和退出码

`--agent` 与 `--output json/jsonl` 使用 Schema v2。每条记录包含 `schemaVersion`、`command`、`type`、`status`、`time`、`result`、`error` 和 `operationId`。结果与结构化错误均写入 stdout，stderr 用于诊断。机器输出不使用颜色、ANSI、动画或终端宽度。

普通命令输出一个 JSON 文档。监视使用 JSONL，周期轮询可使用 JSONL 逐条输出进度，并以结果记录结束；指定 `--output json` 的轮询只返回最终文档。JSONL 每行均为独立 JSON，对载荷中的换行进行转义。会话 JSONL 导出同样逐条保存原始事件。

```powershell
serial-workbench --agent capabilities
serial-workbench --agent schema modbus.read
serial-workbench --agent help
serial-workbench --agent protocol inspect --hex "01 03 00 00 00 01 84 0A"
```

能力发现和 Schema 查询无需 Host。各命令的具体参数和结果契约位于 `schemas/COMMAND.schema.json`；构建脚本通过 `schemas export` 从当前类型与命令树生成契约。错误、普通结果和监视事件的汇总 Schema 分别为 `cli-error.schema.json`、`cli-result.schema.json` 和 `cli-event.schema.json`。

退出码：

| 值 | 含义 |
| ---: | --- |
| `0` | 命令完成或检测通过 |
| `1` | 检测完成但结果未通过，或收到 Modbus 异常响应 |
| `2` | 命令或参数错误 |
| `3` | 串口、IPC 或运行错误 |
| `4` | 操作超时 |
| `5` | 用户取消 |

## 构建

构建环境：

- Windows 11 x64。
- .NET SDK 10.0.400。
- Windows SDK 10.0.26100.0。
- Windows App SDK 2.4.0。

在仓库根目录执行：

```powershell
.\eng\build.ps1
.\eng\test.ps1
.\eng\publish.ps1
```

脚本会按解决方案完整构建。`eng/test.ps1` 运行全部 xUnit 测试，`eng/publish.ps1` 生成 self-contained `win-x64` 便携版。

## 项目结构

```text
src/
├─ SerialWorkbench.Domain          跨层领域模型
├─ SerialWorkbench.Application     事件日志和写入租约
├─ SerialWorkbench.Protocols       HEX 和校验算法
├─ SerialWorkbench.Modbus          Modbus RTU 编解码
├─ SerialWorkbench.Serial.Windows  Windows 串口连接和传输
├─ SerialWorkbench.Storage         应用路径和数据目录
├─ SerialWorkbench.Sessions        SQLite 会话和 CSV 导出
├─ SerialWorkbench.Ipc             Host RPC 契约和客户端
├─ SerialWorkbench.Host            串口服务进程
├─ SerialWorkbench.Cli             命令行入口
└─ SerialWorkbench.WinUI           WinUI 3 桌面入口
```

依赖方向保持为：

```text
Domain
  ↑
Application / Protocols / Sessions
  ↑
Serial.Windows / Storage / IPC / Host
 ↑
WinUI / CLI
```

## 文档

- [产品模型](docs/design.md)
- [系统架构](docs/architecture.md)
- [代码规范](docs/code-style.md)
- [JSON Schema](schemas/)

## 许可证

Apache License 2.0，见 [LICENSE](LICENSE)。
