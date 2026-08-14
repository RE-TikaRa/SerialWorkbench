# 系统架构

## 进程

```text
SerialWorkbench.exe ─┐
                     ├─ Windows 命名管道 ─ SerialWorkbench.Host.exe ─ 串口
serial-workbench.exe ┘                              ├─ 写入租约
                                                    └─ SQLite 会话

```

`SerialWorkbench.Host.exe` 独占串口连接、写入租约、实时事件、会话写入和工作区状态。WinUI 与 CLI 通过版本化本机 RPC 共享 Host 状态。同一应用目录对应一个 Host 实例，最后一个客户端断开后保留 30 秒重连时间。

## 项目边界

依赖方向为：

```text
Domain
  ↑
Application / Protocols / Sessions
  ↑
Serial.Windows / Storage / IPC / Host
  ↑
WinUI / CLI / ProtocolHost
```

`SerialWorkbench.Domain` 定义串口、会话和数据通道模型。`Application` 管理写入租约和事件日志；`Sessions` 管理 SQLite 会话；`Serial.Windows` 访问 Windows 串口；`Host` 组合运行时服务；`IPC` 提供客户端与 Host 的本机 RPC 契约。

## IPC

RPC 契约位于 `src/SerialWorkbench.Ipc/RpcContracts.cs`。握手使用主版本和次版本，Host 根据主版本决定客户端兼容性。RPC 覆盖端口、连接、发送、事件、回环、工作区和会话管理。

## 数据

应用根目录为 `SerialWorkbench.exe` 所在目录。全局设置、资料库、会话、扩展登记、日志和缓存位于 `data/`。工作区保存会话、报告和工作区资产。

每个会话对应一个 `.swbsession` SQLite 文件。事件记录 UTC、单调时钟、递增序号、连接标识、方向、来源和原始字节。会话结束时执行 WAL checkpoint，单个文件可独立迁移和读取。

工作区、连接资料、协议模板、扩展清单和 CLI 输出使用带 `schemaVersion` 的 JSON 文档，其契约位于 `schemas/`。

工作区切换由 Host 执行。客户端提交工作区路径，并使用 Host 返回的数据目录和会话状态更新界面。

## 界面

WinUI 使用 Windows App SDK 原生控件构成功能界面。NavigationView 负责页面选择和窗格状态，MainWindow 持有页面标题、说明和错误提示，Frame 缓存页面主体并呈现导航动画。SelectorBar 切换报文与波形，SettingsCard 呈现设置项，VisualState 根据内容宽度调整布局。

ScottPlot 承载波形图。Light、Dark 和 HighContrast 样式分别使用内置主题或系统颜色。表单页限制字段宽度，工作台数据区与会话主从视图利用剩余空间。

## 构建

`eng/build.ps1` 格式化并构建 Release，`eng/test.ps1` 运行完整构建与 xUnit v3 测试，`eng/publish.ps1` 生成 self-contained `win-x64` 便携版。项目启用 nullable、.NET analyzers、代码风格检查和 warnings-as-errors，依赖版本集中在 `Directory.Packages.props`。
