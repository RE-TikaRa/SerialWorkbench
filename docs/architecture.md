# 系统架构

## 进程

```text
SW.exe     ─┐
SW_TUI.exe ─┼─ Windows 命名管道 ─ SW_HOST.exe ─ 串口
SW_CLI.exe ─┘                            ├─ 写入租约
                                         └─ SQLite 会话

```

`SW_HOST.exe` 独占串口连接、写入租约、实时事件、会话写入和工作区状态。WinUI、TUI 与 CLI 通过版本化本机 RPC 共享 Host 状态。同一应用目录对应一个 Host 实例。有客户端、连接或写入任务时保持运行；全部结束后等待 30 秒退出。客户端退出释放 RPC 通道，共享连接由明确的关闭操作结束。

Host 任务管理器在接受请求前取得写入租约并保存任务身份。设备操作使用 Host 的取消令牌，客户端等待与任务执行具有独立生命周期。显式取消结束任务；进度通过版本号和长轮询读取，结果写入 SQLite。Modbus 扫描和轮询由 Host 编排，客户端呈现采样结果。

`SW_CLI.exe kill` 根据完整可执行路径识别目标应用目录的 GUI、TUI、CLI 与 Host。其他客户端退出后，通过 RPC 取消连接任务、关闭串口并结束会话，再请求 Host 退出。RPC 不可用或关闭超时则结束 Host 进程，结果记录正常或强制退出；执行命令的 CLI 输出结果后退出。命令没有启动 Host 的副作用。

## 项目边界

依赖方向为：

```text
Domain
  ↑
Application / Protocols / Sessions
  ↑
Serial.Windows / Storage / IPC / Host
  ↑
WinUI / TUI / CLI / Agent
```

这里的 Agent 是 `SW_CLI.exe --agent` 的调用方，不是第四种终端客户端。Agent 不启动 TUI、不读取终端画面；CLI 的完整命令树、参数和业务能力都可通过 `--agent` 使用，只把输出切换为 JSON/JSONL 和稳定 Schema。

`SerialWorkbench.Domain` 定义串口、会话和数据通道模型。`Application` 管理写入租约、事件日志和共享报文视图；`Protocols` 提供协议及波形解析；`Sessions` 管理 SQLite 会话；`Serial.Windows` 访问 Windows 串口；`Host` 组合运行时服务；`IPC` 提供客户端与 Host 的本机 RPC 契约。

## IPC

RPC 契约位于 `src/SerialWorkbench.Ipc/RpcContracts.cs`。握手使用主版本和次版本，Host 根据主版本决定客户端兼容性。RPC 覆盖端口、连接与在线控制、共享配置与发送历史、发送与协议任务、任务进度及取消、实时事件、工作区和会话管理。

## 数据

应用根目录默认为各入口所在目录，CLI 与 TUI 可通过 `--app-root` 指定。界面设置、日志、全局会话和 `operations.sqlite3` 位于 `data/`。工作区保存会话。

每个会话对应一个 `.swbsession` SQLite 文件。事件记录 UTC、单调时钟、递增序号、连接标识、方向、来源和原始字节。会话结束时执行 WAL checkpoint，单个文件可独立迁移和读取。

Agent 与 CLI 的 JSON/JSONL 共用 Schema v2，契约记录包含固定信封和命令特定结果；Agent 通过 `SW_CLI.exe --agent <command>` 获取 CLI 全部业务命令的机器结果。结果与结构化错误写 stdout，诊断写 stderr。契约位于 `schemas/`，由命令树和实际结果类型生成。

工作区切换由 Host 执行。客户端提交工作区路径，并使用 Host 返回的数据目录和会话状态更新界面。
Host 将工作区选择保存于应用数据目录，启动时恢复；客户端连接时读取共享状态。

## 界面

终端工作台通过 Terminal.Gui 的 Window、Tabs、TableView、ListView、TextField、选择器和状态栏实现。控件负责焦点、输入、滚动、选择和绘制。WinUI 与 TUI 共享文本、HEX 聚合、复制和波形解析实现，通过同一套 RPC 访问 Host。

`SerialWorkbench.Tui` 提供独立启动入口，工作台实现位于 `SerialWorkbench.Cli/Tui`。布局、导航、设置、报文、发送与任务等 partial 文件组织同一个客户端；`SW_CLI.exe` 无参数显示帮助，`SW_TUI.exe` 无参数进入交互界面。

TUI 的五个主工作区保持稳定，F9 工具按设备操作和数据分析分组打开。Terminal.Gui Scheme 负责焦点、选择、RX/TX 和状态反馈；SpinnerView 与 ProgressBar 负责延迟加载和任务活动显示。没有额外绘制组件或增加运行时依赖。

WinUI 使用 Windows App SDK 原生控件构成功能界面。NavigationView 负责页面选择和窗格状态，MainWindow 持有页面标题、说明和错误提示，Frame 缓存页面主体并呈现导航动画。SelectorBar 切换报文与波形，SettingsCard 呈现设置项，VisualState 根据内容宽度调整布局。

ScottPlot 承载波形图。Light、Dark 和 HighContrast 样式分别使用内置主题或系统颜色。表单页限制字段宽度，工作台数据区与会话主从视图利用剩余空间。

## 构建

`eng/build.ps1` 格式化并构建 Release，`eng/test.ps1` 运行完整构建与 xUnit v3 测试，`eng/publish.ps1` 生成 self-contained `win-x64` 便携版。项目启用 nullable、.NET analyzers、代码风格检查和 warnings-as-errors，依赖版本集中在 `Directory.Packages.props`。

GUI 通过程序集名称生成 `SW.exe` 与 `SW.pri`。CLI、TUI 和 Host 的 `AppHostName` 指定原生启动程序名称，`Directory.Build.targets` 设置 SDK 构建项的目标路径；托管程序集保持各自项目名称。发布脚本合并四个项目的产物。
