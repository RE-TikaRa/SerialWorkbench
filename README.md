# SerialWorkbench

SerialWorkbench 是面向 Windows 11 的串口调试、通信记录和 TX-RX 回环检测工作台。桌面界面与命令行共用独立的 `SerialWorkbench.Host.exe`，串口句柄、写入租约和会话数据库始终由 Host 管理。

当前项目名称可以调整。功能模块、IPC 契约和 WinUI 工程均使用清晰的程序集边界，不依赖目录名 `CableTester`。

## 功能

- 枚举 Windows 串口并显示端口名称与设备友好名称。
- 配置波特率、数据位、停止位、校验、握手、DTR 和 RTS。
- 文本与 HEX 发送，支持 CR、LF 和 CRLF 行尾。
- 按时间顺序监视 TX/RX 原始数据，可切换文本或 HEX 显示。
- 暂停视图、清空视图、复制完整 HEX 记录和实时收发计数。
- 固定、递增和随机数据的 TX-RX 回环检测。
- 每个会话保存为独立 `.swbsession` SQLite 文件。
- 应用目录数据与工作区数据分离。
- CLI 提供端口枚举、Host 状态、工作区、发送、监视和回环命令。
- 内置 HEX、XOR、SUM8、CRC-16/Modbus、CRC-16/XMODEM、CRC-32、流式成帧、Modbus RTU、XMODEM 块、ANSI/VT 状态和自动化基础模块。

## 进程结构

```text
SerialWorkbench.exe ─┐
                     ├─ Windows 命名管道 ─ SerialWorkbench.Host.exe ─ 串口
serial-workbench.exe ┘                              ├─ 写入租约
                                                    └─ SQLite 会话

SerialWorkbench.ProtocolHost.exe ─ 独立协议扩展宿主
```

WinUI 与 CLI 不打开串口，也不写入会话数据库。同一应用目录只运行一个 Host；最后一个客户端异常断开后，Host 保留 30 秒重连时间。

## 界面规则

通用界面只使用 WinUI 3 和 Windows App SDK 原生组件：

- `NavigationView` 负责功能导航。
- `TabView` 负责工作视图切换。
- `ListView` 与共享 `DataTemplate` 显示收发数据。
- `CommandBar`、`AppBarButton`、`ComboBox`、`NumberBox`、`ToggleSwitch`、`InfoBar`、`InfoBadge` 和 `ScrollView` 提供操作与状态。
- 同类按钮、标题和数据行复用应用级样式与模板。

项目不使用 Canvas、Win2D、Direct2D、自绘通用控件或第三方通用控件库。`ScottPlot.WinUI` 只允许用于时间序列、XY、直方图和 FFT 等工程图表画布。

## 数据目录

未打开工作区时：

```text
<便携目录>/
├─ SerialWorkbench.exe
├─ SerialWorkbench.Host.exe
├─ serial-workbench.exe
├─ SerialWorkbench.ProtocolHost.exe
└─ data/
   ├─ settings/
   ├─ library/
   ├─ sessions/
   ├─ reports/
   ├─ extensions/
   ├─ logs/
   └─ cache/
```

打开工作区后：

```text
<工作区>/
├─ sessions/
└─ reports/
```

全局设置、资料库和扩展登记仍保存在 `<便携目录>/data/`。会话和报告进入工作区。切换工作区前需要关闭串口连接。

应用不会把受管理数据写入 AppData、LocalAppData、ProgramData、注册表配置区或用户文档目录。文件夹选择器、剪贴板等 Windows 系统功能仍按系统自身规则工作。

## 便携版

执行：

```powershell
.\eng\publish.ps1
```

输出目录：

```text
publish\win-x64\
```

复制整个目录即可运行。入口文件为 `SerialWorkbench.exe`。Host、CLI、协议宿主、Windows App SDK、应用 PRI、XBF、Schema、README 和许可证都包含在同一目录。

便携目录必须可写，因为默认 `data/` 位于 EXE 旁边。

## 图形界面

1. 启动 `SerialWorkbench.exe`。
2. 选择串口和通信参数。
3. 点击“连接”。
4. 在“接收”工作台底部发送文本或 HEX。
5. 在同一工作台查看 TX/RX 数据。
6. TX 与 RX 回接时，在“回环检测”运行固定、递增或随机数据检测。
7. 在“设置”中选择工作区；关闭工作区后，会话重新保存到便携目录的 `data/sessions/`。

## 命令行

CLI 会按需启动同目录 Host。

列出端口：

```powershell
.\serial-workbench.exe ports list
.\serial-workbench.exe ports list --output json
```

查看 Host 和数据目录：

```powershell
.\serial-workbench.exe host status --output json
```

设置、查看和关闭工作区：

```powershell
.\serial-workbench.exe workspace set --path E:\Workspaces\DeviceA
.\serial-workbench.exe workspace show --output json
.\serial-workbench.exe workspace clear
```

发送文本或 HEX：

```powershell
.\serial-workbench.exe send --port COM17 --baud 115200 --text "hello" --line-ending crlf
.\serial-workbench.exe send --port COM17 --baud 115200 --hex "55 AA 01 02"
```

监视串口：

```powershell
.\serial-workbench.exe monitor --port COM17 --baud 115200 --seconds 10
.\serial-workbench.exe monitor --port COM17 --baud 115200 --seconds 10 --output jsonl
```

运行回环检测：

```powershell
.\serial-workbench.exe loopback run --port COM17 --baud 115200 --pattern Fixed --length 4096 --iterations 4
.\serial-workbench.exe loopback run --port COM17 --baud 115200 --pattern Incrementing --length 65536 --iterations 2 --output json
.\serial-workbench.exe loopback run --port COM17 --baud 115200 --pattern Random --length 1048576 --iterations 1 --output json
```

停止 Host：

```powershell
.\serial-workbench.exe host stop
```

退出码：

| 值 | 含义 |
| ---: | --- |
| `0` | 命令完成或检测通过 |
| `1` | 检测执行完成但结果未通过 |
| `2` | 命令、参数或交互输入不完整 |
| `3` | Host、串口、IPC 或运行错误 |
| `4` | 超时 |
| `5` | 用户取消 |

JSON 与 JSON Lines 输出的契约位于 `schemas/cli-result.schema.json`、`schemas/cli-event.schema.json` 和 `schemas/cli-error.schema.json`。

## 会话文件

每个活动会话对应一个 `.swbsession` SQLite 文件。数据库保存：

- UTC 时间。
- 单调时钟值。
- 全局递增序号。
- 连接标识。
- TX/RX 方向。
- 数据来源。
- 原始字节。

派生文本、HEX 和协议视图不会覆盖原始字节。会话关闭时执行 WAL checkpoint，文件可以单独复制和离线读取。

## JSON 资产

`schemas/` 包含工作区、连接资料、发送队列、测试序列、协议模板、扩展清单和 CLI 输出的 JSON Schema。所有资产都带 `schemaVersion`，用于明确格式迁移边界。

## 项目结构

```text
src/
├─ SerialWorkbench.Domain/                  领域模型
├─ SerialWorkbench.Application/             事件日志与写入租约
├─ SerialWorkbench.Ipc/                     Host RPC 契约与客户端
├─ SerialWorkbench.Serial.Windows/          Windows 串口实现
├─ SerialWorkbench.Protocols/               HEX、校验与成帧
├─ SerialWorkbench.Automation/              发送编译与并行结果
├─ SerialWorkbench.Terminal/                ANSI/VT 状态
├─ SerialWorkbench.Modbus/                  Modbus RTU 编解码
├─ SerialWorkbench.Transfer/                XMODEM 块编解码
├─ SerialWorkbench.Sessions/                SQLite 会话
├─ SerialWorkbench.Storage/                 应用目录与工作区路径
├─ SerialWorkbench.Extensions.Abstractions/ 协议扩展契约
├─ SerialWorkbench.Monitoring.Abstractions/ 监视源契约
├─ SerialWorkbench.Host/                    应用功能内核
├─ SerialWorkbench.ProtocolHost/            协议扩展宿主
├─ SerialWorkbench.Cli/                     命令行客户端
└─ SerialWorkbench.WinUI/                   WinUI 3 客户端

tests/SerialWorkbench.Tests/                单元、存储和架构测试
schemas/                                    JSON Schema
eng/                                        构建、测试和发布脚本
docs/adr/                                   架构决定
```

## 开发环境

- Windows 11 x64。
- .NET SDK `10.0.400`。
- Windows SDK `10.0.26100.0`。
- Windows App SDK `2.4.0`。

依赖版本集中在 `Directory.Packages.props`。项目开启 nullable、.NET analyzers、代码风格检查和 warnings-as-errors。

## 构建与测试

格式化并执行完整 Release 构建：

```powershell
.\eng\build.ps1
```

完整构建后运行 Microsoft Testing Platform 与 xUnit v3：

```powershell
.\eng\test.ps1
```

也可以使用底层命令：

```powershell
dotnet format SerialWorkbench.slnx
dotnet build SerialWorkbench.slnx --configuration Release
dotnet test SerialWorkbench.slnx --configuration Release --no-build
```

## 串口排障

- 端口未显示：确认设备管理器中的“端口 (COM 和 LPT)”存在目标设备，然后重新启动 Host 或应用。
- 端口被占用：关闭持有该串口的其他程序，再重新连接。
- 文本乱码：CLI 使用 `--encoding` 指定编码；HEX 视图始终保留原始字节。
- 回环超时：确认 TX 与 RX 已正确回接，且波特率、校验、停止位和流控一致。
- 便携程序启动即退出：检查发布目录是否包含 `SerialWorkbench.pri`、`App.xbf`、`MainWindow.xbf` 和 Windows App SDK 文件。
- 无法创建 `data/`：将整个便携目录放到具有写权限的位置。

## 许可证

Apache License 2.0，见 [LICENSE](LICENSE)。
