# SerialWorkbench

SerialWorkbench 是面向 Windows 11 的串口调试与协议工作台，服务于设备连接、原始报文、协议事务、会话记录和自动化操作。

项目由四个入口组成，共享同一个 Host：

```text
SW.exe       WinUI 3 图形工作台
SW_TUI.exe   Terminal.Gui 终端工作台
SW_CLI.exe   CLI 与 Agent 接口
SW_HOST.exe  串口、任务和会话服务
```

```text
WinUI / TUI / CLI / Agent
          │
          ▼
    Windows named pipe
    StreamJsonRpc
          │
          ▼
    SerialWorkbench.Host
          │
          ├─ SerialPort
          ├─ EventJournal
          └─ SQLite sessions
```

GUI 面向完整的人机操作，TUI 聚焦终端中的高频收发，CLI 面向脚本与 CI，Agent 通过 `SW_CLI.exe --agent` 调用 CLI 的完整命令树，不进入 TUI 或解析终端画面。三种客户端不复制串口和会话业务，连接状态由 Host 统一管理。

## 快速开始

先生成便携版：

```powershell
.\eng\publish.ps1
```

从发布目录启动：

```powershell
.\publish\win-x64\SW.exe
.\publish\win-x64\SW_TUI.exe
.\publish\win-x64\SW_CLI.exe --help
.\publish\win-x64\SW_CLI.exe --agent capabilities
```

执行通信前先枚举当前设备：

```powershell
.\publish\win-x64\SW_CLI.exe ports list --output json
```

关闭当前发布目录的连接、客户端和 Host：

```powershell
.\publish\win-x64\SW_CLI.exe --agent kill
```

`kill` 输出结果后会结束执行它的 CLI。它只识别同一应用目录中的 `SW.exe`、`SW_TUI.exe`、`SW_CLI.exe` 和 `SW_HOST.exe`，不会影响其他目录的安装。

## 能力范围

| 领域 | 能力 |
| --- | --- |
| 串口 | 端口枚举、设备身份、波特率、校验、停止位、流控、DTR/RTS、BREAK、RS-485 |
| 收发 | 文本与 HEX、编码、行尾、校验追加、循环发送、RX/TX 监视、来源筛选 |
| 连接 | 多连接、共享 `connectionId`、设备重连、连接分段、写入租约 |
| 协议 | Modbus RTU、通用 JSON 协议模板、帧长度、字段、大小端、校验 |
| 任务 | 自动化序列、回环、Modbus 扫描与轮询、XMODEM-CRC、后台任务 |
| 会话 | SQLite 会话、原始事件、筛选、CSV/JSONL/TXT/HEX/binary 导出、回放 |
| 分析 | 文本分行、HEX 聚合、波形解析、报文复制和多选 |

原始串口字节先进入事件和会话，再派生为文本、HEX、波形或协议结果。读取块不等于协议帧。

## TUI

TUI 使用 Terminal.Gui 原生控件，不自行绘制组件，不引入额外 TUI 框架，也不使用装饰性 emoji。

主工作区保持五个页面：

```text
Alt+1  工作台       连接、报文、发送
Alt+2  发送历史     载入常用输入
Alt+3  任务         进度、结果、取消
Alt+4  会话         筛选、导出、回放
Alt+5  设置         连接、显示、发送配置
```

常用按键：

```text
F1       帮助             F2       暂停显示
F3       清空报文         F4       连接管理
F5       刷新端口         F6       复制所选报文
F7       返回实时视图     F8       设置
F9       工具             Ctrl+Q  退出
```

F9 工具分为两组：

```text
设备操作：Modbus、传输与回环、自动化
数据分析：协议分析、波形
```

工作台在 80 列以上使用连接与报文两栏；60 至 79 列优先保留报文区域；低于 60×20 显示尺寸提示。主题、焦点、选择、加载反馈和任务进度全部使用 Terminal.Gui 的 Scheme、SpinnerView 和 ProgressBar。

HEX 输入每两个十六进制数字表示一个字节：

```text
正确：0A
正确：01 03 00 00 00 01
正确：0x01, 02-0A:FF
```

奇数位输入不会自动补位。客户端会提示“每两个十六进制数字表示一个字节，例如 0A”，避免改变设备数据含义。

## CLI 与 Agent

CLI 使用 System.CommandLine 管理命令、参数、Help、补全和 Response File。一次性文本结果使用 Spectre.Console；机器输出使用 Schema v2。`SW_CLI.exe` 和 `SW_CLI.exe --agent` 使用同一套命令、参数和业务能力，Agent 只切换到稳定的机器输出，不依赖 TUI 的布局、快捷键或终端状态。

常用命令：

```powershell
# 端口和 Host
SW_CLI.exe ports list --output json
SW_CLI.exe host status --output json

# 共享连接
SW_CLI.exe connections open --port COM20 --baud 115200 --output json
SW_CLI.exe connections list --output json
SW_CLI.exe connections close --id CONNECTION_ID --output json

# 发送与监视
SW_CLI.exe send --port COM20 --baud 115200 --hex "01 03 00 00 00 01" --output json
SW_CLI.exe monitor --port COM20 --baud 115200 --seconds 10 --output jsonl

# 协议检查
SW_CLI.exe protocol inspect --hex "01 03 02 00 0A 38 43" --output json

# 会话
SW_CLI.exe sessions list --output json
SW_CLI.exe sessions export --id SESSION_ID --file E:\Exports\session.csv --output json

# 关闭当前应用目录
SW_CLI.exe --agent kill
```

Agent 能力和命令 Schema 不需要启动 Host：

```powershell
SW_CLI.exe --agent capabilities
SW_CLI.exe --agent schema modbus.read
SW_CLI.exe --agent schema kill
SW_CLI.exe --agent help

# CLI 的完整业务命令同样支持 Agent 输出
SW_CLI.exe --agent ports list
SW_CLI.exe --agent send --port COM20 --baud 115200 --hex "01 03 00 00 00 01"
SW_CLI.exe --agent modbus read --port COM20 --baud 115200 --slave 1 --address 0 --quantity 1 --function 3
SW_CLI.exe --agent sessions export --id SESSION_ID --file E:\Exports\session.jsonl --format jsonl
```

机器输出具有固定信封：

```json
{
  "schemaVersion": 2,
  "command": "ports.list",
  "type": "result",
  "status": "success",
  "result": {}
}
```

结果和结构化错误写入 stdout，诊断写入 stderr。JSONL 一行一条记录，不依赖终端宽度、颜色或动画。

## 数据目录

应用根目录是入口程序所在目录。Host 使用同目录的 `data/` 保存全局设置、日志、任务和会话：

```text
data/
├─ settings/
│  ├─ serial-preferences.json
│  ├─ serial-profiles.json
│  ├─ send-history.json
│  └─ workspace.json
├─ sessions/
│  └─ *.swbsession
├─ logs/
└─ operations.sqlite3
```

选择工作区后，新会话写入工作区的 `sessions/`，应用级设置仍保存在应用目录的 `data/settings/`。

## 构建与验证

环境要求：

```text
Windows 11 x64
.NET SDK 10.0.400
Windows SDK 10.0.26100.0
Windows App SDK 2.4.0
```

仓库脚本：

```powershell
.\eng\build.ps1       # 格式化并构建 Release
.\eng\test.ps1        # 完整构建与测试
.\eng\publish.ps1     # 生成 publish/win-x64
```

发布目录是 self-contained 便携版，必须整体保留 `SW.pri`、XBF、Windows App SDK 资源、Host/CLI/TUI 托管程序集和 `data/`。构建配置直接生成四个 EXE，不依赖发布后的手工改名。

## 文档

- [系统架构](docs/architecture.md)：进程、Host、RPC、数据和构建边界
- [产品模型](docs/design.md)：连接、会话、协议、界面和 CLI 语义
- [功能边界与后续增强](docs/feature-gap-analysis.md)：当前能力、待增强内容和验证边界
- [代码规范](docs/code-style.md)：分层、异步、测试、文档和提交约定
- [JSON Schema](schemas/)：CLI 与 Agent 的机器契约

## 许可证

Apache License 2.0，见 [LICENSE](LICENSE)。
