# SerialWorkbench

SerialWorkbench 是面向 Windows 11 的串口调试、通信记录和回环检测工作台。

## 功能

- 枚举 Windows 串口并配置波特率、数据位、停止位、校验、流控、DTR 和 RTS。
- 发送文本或 HEX 数据，支持 CR、LF、CRLF、定时循环发送和校验计算。
- 按时间顺序查看 TX/RX 报文、完整 HEX、实时计数和数据波形。
- 编解码 Modbus RTU 请求与响应。
- 使用固定、递增或随机数据执行 TX-RX 回环检测。
- 在便携数据目录或工作区中保存独立的 `.swbsession` 会话文件。
- 浏览会话、预览事件、导出完整 CSV、按原始时间间隔回放、定位文件并删除已结束的会话。
- 保存和应用命名串口连接配置。
- 使用独立串口终端进行文本或 HEX 交互，支持行尾、历史和响应复制。
- 通过 CLI 枚举端口、管理工作区、发送、监视和运行回环检测。

## 运行

发布目录的桌面入口为 `SerialWorkbench.exe`，命令行入口为 `serial-workbench.exe`。

应用根目录是 `SerialWorkbench.exe` 所在目录。全局设置和会话位于 `data/`；打开工作区后，会话和报告写入工作区。便携目录需要写入权限。

## 命令行

```powershell
.\serial-workbench.exe ports list
.\serial-workbench.exe workspace set --path E:\Workspaces\DeviceA
.\serial-workbench.exe send --port COM3 --baud 115200 --hex "55 AA 01 02"
.\serial-workbench.exe monitor --port COM3 --baud 115200 --seconds 10 --output jsonl
.\serial-workbench.exe loopback run --port COM3 --baud 115200 --pattern Incrementing --length 4096 --iterations 4
.\serial-workbench.exe modbus read --port COM3 --baud 115200 --slave 1 --address 0 --quantity 1
```

CLI 退出码：

| 值 | 含义 |
| ---: | --- |
| `0` | 命令完成或检测通过 |
| `1` | 检测完成但结果未通过 |
| `2` | 命令或参数错误 |
| `3` | 串口或运行错误 |
| `4` | 超时 |
| `5` | 用户取消 |

## 构建

构建环境：

- Windows 11 x64
- .NET SDK `10.0.400`
- Windows SDK `10.0.26100.0`
- Windows App SDK `2.4.0`

```powershell
.\eng\build.ps1
.\eng\test.ps1
.\eng\publish.ps1
```

`eng/publish.ps1` 在 `publish/win-x64/` 生成 self-contained 便携版。

## 文档

- [产品设计](docs/design.md)
- [系统架构](docs/architecture.md)
- [代码规范](docs/code-style.md)
- [JSON Schema](schemas/)

## 许可证

Apache License 2.0，见 [LICENSE](LICENSE)。
