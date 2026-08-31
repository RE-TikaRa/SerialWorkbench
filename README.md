# SerialWorkbench

SerialWorkbench 是面向 Windows 11 的串口调试工作台。它把串口连接、原始报文、协议解析、会话记录和自动化操作放在同一条调用链中，适合单片机、USB 转串口、RS-232、RS-485 和 Modbus RTU 设备调试。

## 能做什么

- 枚举 Windows 串口，并显示端口描述、VID、PID 和设备实例标识。
- 配置端口、波特率、数据位、校验、停止位、流控、DTR 和 RTS。
- 保存、重命名、删除和应用命名串口连接配置。
- 为连接配置 `DUT`、`DEBUG`、`CONTROLLER` 或 `LOOPBACK` 逻辑角色。
- 发送文本或 HEX 数据，设置文本编码、CR、LF、CRLF 和校验追加。
- 按固定间隔循环发送，并在同一报文流中查看 TX 和 RX。
- 使用文本或 HEX 监视报文，查看时间戳、完整原始字节和实时计数。
- 将接收数据解析为 CSV 文本或固定长度二进制波形。
- 使用交互式串口终端发送数据、浏览输入历史和复制响应。
- 构造并执行 Modbus RTU 读寄存器、读输入寄存器和写单寄存器事务。
- 区分 TX 回显、无关 RX、合法响应、CRC 错误、异常响应和超时。
- 使用协议帧查看器检查地址、功能码、长度、CRC 和异常码。
- 按固定、递增或随机数据执行 RX-TX 回环检测，保存差异位置、耗时和吞吐率。
- 编辑、保存、运行和取消多步骤自动化发送序列。
- 使用 XMODEM-CRC 发送和接收文件，显示块数、重试次数、速度和错误。
- 保存独立的 SQLite 会话文件，查看完整事件、导出 CSV 和按原始时间间隔回放。
- 通过 CLI 执行端口枚举、工作区管理、会话查询与导出、发送、监视、回环、Modbus 和 XMODEM 操作。

所有功能共享 Host 的串口连接和写入租约。原始字节先写入事件和会话，再派生为文本、HEX、波形或协议结果。

## 运行结构

发布目录中有三个入口：

```text
SerialWorkbench.exe       WinUI 3 桌面程序
serial-workbench.exe      命令行程序
SerialWorkbench.Host.exe  串口和会话服务
```

桌面程序和 CLI 通过 Windows 命名管道连接 Host。一个应用目录对应一个 Host 实例，同一串口不能被重复打开。发送、回环、Modbus、自动化和 XMODEM 操作通过连接写入租约串行执行。

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

- 端口和串口参数。
- 命名连接配置。
- 连接逻辑角色。
- 文本或 HEX 报文监视。
- 时间戳、暂停显示、清空和复制 HEX。
- CSV 文本或固定二进制帧波形。
- 文本或 HEX 发送。
- CR、LF、CRLF 行尾。
- XOR、SUM8、CRC16-Modbus、CRC16-XModem 和 CRC32 校验追加。
- 定时循环发送。
- 连接后在线切换 DTR/RTS、清空 RX/TX 缓冲和发送 100 ms BREAK。

暂停只影响工作台显示，串口事件仍由 Host 接收并保存。终端、Modbus、回环和自动化页面复用当前连接。

### 终端

终端提供独立的交互式输入区，但不创建第二条串口通道。支持：

- 文本和 HEX 输入。
- 当前串口编码。
- 无行尾、CR、LF、CRLF。
- 输入历史。
- 清空输入。
- 复制响应。
- 清空终端显示。

终端 TX 会进入普通事件记录，接收数据同时出现在终端和工作台报文流中。

### Modbus RTU

页面可以构造并发送：

- 功能码 03：读保持寄存器。
- 功能码 04：读输入寄存器。
- 功能码 06：写单个保持寄存器。

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
  "data": [1, 3, 0, 0, 0, 1, 132, 10],
  "format": "hex",
  "delayMilliseconds": 100,
  "repeatCount": 2,
  "waitMilliseconds": 0
}
```

序列还包含名称和步骤列表。运行期间所有 TX 进入普通会话事件，取消后不会发送后续步骤。

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
- 查看完整原始事件。
- 查看回环统计。
- 导出完整 CSV。
- 按原始事件时间间隔回放。
- 暂停、继续和停止回放。
- 在资源管理器中定位会话文件。
- 删除已经结束的会话。

回放只写入工作台和终端的界面事件流，不打开串口，不产生 TX，也不会混入原会话。

## 后续增强

### 多连接与设备生命周期

- 在 WinUI 中同时管理多条连接，每条连接拥有独立报文流、发送队列、筛选器和统计。
- 使用 `DUT`、`DEBUG`、`CONTROLLER`、`LOOPBACK` 等逻辑角色组织设备和任务。
- 根据 `DeviceInstanceId` 绑定设备配置，支持设备拔出、插回识别和自动重连。
- 配置重连间隔、重连次数、会话分段和状态提示。
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
--dtr
--rts
```

### 监视

```powershell
.\serial-workbench.exe monitor --port COM15 --baud 115200 --seconds 10 --output jsonl
```

文本输出显示 UTC 时间、方向和 HEX。`jsonl` 每行输出一个带 `schemaVersion` 的结构化事件。

### 回环

```powershell
.\serial-workbench.exe loopback run --port COM15 --baud 115200 --pattern Incrementing --length 4096 --iterations 4 --timeout 5000 --output json
```

可选模式为 `Fixed`、`Incrementing` 和 `Random`。还可以使用 `--seed` 固定随机数据种子。

### Modbus

读保持寄存器或输入寄存器：

```powershell
.\serial-workbench.exe modbus read --port COM15 --baud 115200 --slave 1 --address 0 --quantity 1 --function 3 --timeout 2000 --output json
```

写单个寄存器：

```powershell
.\serial-workbench.exe modbus write --port COM15 --baud 115200 --slave 1 --address 0 --value 1 --timeout 2000 --output json
```

Modbus JSON 结果包含请求帧、响应帧、功能码、寄存器、地址、寄存器值、异常码、耗时和错误信息。

### 协议解析

使用与 WinUI 协议帧查看器相同的 Modbus RTU 解析器：

```powershell
.\serial-workbench.exe protocol inspect --hex "01 03 02 00 0A 38 43" --output json
```

命令会返回地址、功能码、帧长、CRC、异常码和解析失败原因。解析失败时退出码为 `1`，原始 HEX 不会被修改。

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

使用 `--output json` 或 `--output jsonl` 时，结果带有 `schemaVersion`、命令名称、UTC 时间和结果对象。错误输出使用 `schemas/cli-error.schema.json`，普通结果使用 `schemas/cli-result.schema.json`，监视事件使用 `schemas/cli-event.schema.json`。

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
