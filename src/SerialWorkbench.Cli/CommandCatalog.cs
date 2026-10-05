using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using SerialWorkbench.Domain;
using SerialWorkbench.Protocols;

namespace SerialWorkbench.Cli;

public sealed class CommandCatalog
{
    private readonly Dictionary<string, Option> globalOptions = [];
    private readonly Dictionary<string, object?> globalDefaults = [];
    private readonly List<CommandDefinition> commands = [];

    public CommandCatalog()
    {
        AddGlobal("--output", "输出格式", "text", ["text", "json", "jsonl"]);
        AddGlobal("--culture", "界面语言", System.Globalization.CultureInfo.CurrentUICulture.Name);
        AddGlobal("--app-root", "应用目录", AppContext.BaseDirectory);
        var agent = new Option<bool>("--agent") { Description = "使用机器接口", Recursive = true };
        Root.Options.Add(agent);
        globalOptions.Add(agent.Name, agent);
        globalDefaults.Add(agent.Name, false);

        Add("capabilities", "查询能力描述");
        Add("version", "查询版本");
        var schema = Add("schema", "查询命令的 JSON Schema");
        schema.Command.Arguments.Add(new Argument<string>("command"));
        var help = Add("help", "查询命令和参数说明");
        help.Command.Arguments.Add(new Argument<string?>("command") { Arity = ArgumentArity.ZeroOrOne });
        var schemaExport = Add("schemas.export", "导出命令契约");
        Text(schemaExport, "--path", "Schema 导出目录", required: true);

        Add("ports.list", "枚举串口");
        Add("host.status", "查询 Host 状态");
        Add("host.stop", "停止 Host");
        Add("workspace.show", "查询工作区");
        Text(Add("workspace.set", "选择工作区"), "--path", "工作区路径", required: true);
        Add("workspace.clear", "使用全局工作区");
        Add("profiles.list", "查询共享连接配置");
        Text(Add("profiles.show", "查看连接配置"), "--name", "配置名称", required: true);
        var profileSave = Add("profiles.save", "保存连接配置");
        Text(profileSave, "--name", "配置名称", required: true);
        Text(profileSave, "--original-name", "修改的原配置名称");
        SerialOptions(profileSave, true);
        var profileRename = Add("profiles.rename", "重命名连接配置");
        Text(profileRename, "--name", "原配置名称", required: true);
        Text(profileRename, "--new-name", "新配置名称", required: true);
        Text(Add("profiles.delete", "删除连接配置"), "--name", "配置名称", required: true);
        Add("history.list", "查询共享发送历史");
        Add("connections.list", "查询共享连接");
        var open = Add("connections.open", "打开持久连接");
        SerialOptions(open, true);
        open.Options["--port"].Required = false;
        Text(open, "--profile", "使用的连接配置名称");
        open.Command.Validators.Add(result =>
        {
            var hasPort = result.GetResult("--port") is OptionResult { Implicit: false };
            var hasProfile = result.GetResult("--profile") is OptionResult { Implicit: false };
            if (hasPort == hasProfile)
            {
                result.AddError("打开连接需要指定 --port 或 --profile 中的一项。");
            }
            else if (hasProfile
                && open.Command.Options.Any(option => option.Name != "--profile" && result.GetResult(option) is { Implicit: false }))
            {
                result.AddError("不能同时指定 --profile 和串口参数。");
            }
        });
        Id(Add("connections.close", "关闭共享连接"));
        Id(Add("connections.reconnect", "按设备身份恢复连接"));
        var lines = Add("connections.control-lines", "在线更新 DTR/RTS");
        Id(lines);
        foreach (var name in new[] { "--dtr", "--rts" })
        {
            lines.Add(new Option<bool?>(name) { Description = name == "--dtr" ? "DTR 状态，true 或 false" : "RTS 状态，true 或 false", Arity = ArgumentArity.ExactlyOne });
        }
        lines.Command.Validators.Add(result =>
        {
            if (result.GetResult("--dtr") is not OptionResult { Implicit: false } && result.GetResult("--rts") is not OptionResult { Implicit: false })
            {
                result.AddError("至少指定 --dtr 或 --rts。");
            }
        });
        var clearBuffers = Add("connections.clear-buffers", "清空串口缓冲");
        Id(clearBuffers);
        clearBuffers.Add(new Option<bool>("--rx") { Description = "清空接收缓冲" }, false);
        clearBuffers.Add(new Option<bool>("--tx") { Description = "清空发送缓冲" }, false);
        clearBuffers.Command.Validators.Add(result =>
        {
            if (!result.GetValue<bool>("--rx") && !result.GetValue<bool>("--tx"))
            {
                result.AddError("至少选择 --rx 或 --tx。");
            }
        });
        var sendBreak = Add("connections.break", "发送 BREAK 信号");
        Id(sendBreak);
        Integer(sendBreak, "--duration", "持续时间，单位毫秒", 100, 1, 10_000);
        Add("operations.list", "查询任务");
        Id(Add("operations.show", "查询任务状态"));
        Id(Add("operations.result", "查询任务结果"));
        Id(Add("operations.cancel", "取消任务"));
        var progress = Add("operations.progress", "分页查询任务进度");
        Id(progress);
        LongInteger(progress, "--after", "上次读取的进度版本", 0, 0, long.MaxValue);
        Integer(progress, "--count", "最多返回的进度条数", 1000, 1, 10_000);
        Integer(progress, "--wait", "等待新进度的时间，单位毫秒", 0, 0, 30_000);
        var wait = Add("operations.wait", "等待已有任务完成");
        Id(wait);
        Integer(wait, "--timeout", "等待超时，单位毫秒", 30_000, 1, 600_000);
        var operation = Add("operations.start", "启动后台任务");
        GuidOption(operation, "--connection", "共享连接标识", true);
        Text(operation, "--kind", "任务命令", required: true);
        Text(operation, "--parameters", "JSON 参数", "{}");
        Text(operation, "--file", "JSON 参数文件");
        Text(operation, "--request-id", "持久请求标识");
        Add("sessions.list", "查询会话");
        Id(Add("sessions.show", "查看会话事件"));
        Id(Add("sessions.delete", "删除会话"));
        var export = Add("sessions.export", "导出会话");
        Id(export);
        Text(export, "--file", "导出路径", required: true);
        Text(export, "--format", "导出格式", "csv", choices: ["csv", "jsonl", "text", "hex", "binary"]);
        Filters(export);
        Text(export, "--hex", "匹配的 HEX 片段");

        var send = Add("send", "发送文本或 HEX");
        SerialOptions(send);
        SendOptions(send);
        var repeat = Add("send.repeat", "按间隔循环发送文本或 HEX");
        SerialOptions(repeat);
        SendOptions(repeat);
        Integer(repeat, "--interval", "发送间隔，单位毫秒", 1000, 1, 600_000);
        Integer(repeat, "--count", "发送次数，0 持续运行", 1, 0, 100_000);

        var monitor = Add("monitor", "实时监视新增报文");
        SerialOptions(monitor);
        Integer(monitor, "--seconds", "监视时长，单位秒", 10, 1, 86400);
        Filters(monitor);

        var loopback = Add("loopback.run", "运行回环检测");
        SerialOptions(loopback);
        Integer(loopback, "--length", "每次发送的字节数", 4096, 1, 16 * 1024 * 1024);
        Integer(loopback, "--iterations", "检测次数", 1, 1, 10_000);
        Integer(loopback, "--timeout", "响应超时，单位毫秒", 5000, 1, 600_000);
        Integer(loopback, "--seed", "随机数据种子", 0x534257, int.MinValue, int.MaxValue);
        EnumOption(loopback, "--pattern", "数据模式", LoopbackPattern.Incrementing);

        var read = Add("modbus.read", "读取 Modbus 位或寄存器");
        ModbusOptions(read, 3, [1, 2, 3, 4, 17]);
        var write = Add("modbus.write", "写入 Modbus 位或寄存器");
        ModbusOptions(write, 6, [5, 6, 15, 16]);
        Integer(write, "--value", "单个值", -1, 0, ushort.MaxValue);
        Text(write, "--values", "多个值，以逗号分隔");
        write.Command.Validators.Add(result =>
        {
            var function = 6;
            if (result.GetResult("--function") is { Tokens.Count: > 0 } functionResult
                && !int.TryParse(functionResult.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out function))
            {
                return;
            }

            if (function is 5 or 6 ? result.GetResult("--value") is not OptionResult { Implicit: false } : result.GetResult("--values") is not OptionResult { Implicit: false })
            {
                result.AddError(function is 5 or 6 ? "单个写入需要 --value。" : "多个写入需要 --values。");
            }
        });

        var scan = Add("modbus.scan", "扫描 Modbus 从站");
        SerialOptions(scan);
        Integer(scan, "--from", "起始从站", 1, 1, 247);
        Integer(scan, "--to", "结束从站", 247, 1, 247);
        Integer(scan, "--address", "读取寄存器地址", 0, 0, ushort.MaxValue);
        Integer(scan, "--timeout", "响应超时，单位毫秒", 200, 1, 600_000);
        Integer(scan, "--interval", "扫描间隔，单位毫秒", 0, 0, 600_000);
        var poll = Add("modbus.poll", "周期读取 Modbus 数据");
        ModbusOptions(poll, 3, [1, 2, 3, 4]);
        Integer(poll, "--count", "采样次数", 10, 1, 100_000);
        Integer(poll, "--interval", "采样间隔，单位毫秒", 1000, 0, 600_000);

        foreach (var action in new[] { "send", "receive" })
        {
            var xmodem = Add($"xmodem.{action}", action == "send" ? "发送 XMODEM 文件" : "接收 XMODEM 文件");
            SerialOptions(xmodem);
            Text(xmodem, "--file", "文件路径", required: true);
        }

        var sequence = Add("sequence.run", "执行自动化序列");
        SerialOptions(sequence);
        Text(sequence, "--file", "序列 JSON 文件", required: true);

        var inspect = Add("protocol.inspect", "离线检查协议帧");
        Text(inspect, "--hex", "帧 HEX", required: true);
        Text(inspect, "--template", "通用协议模板路径");

        foreach (var definition in commands.Where(static item => item.Id is "send" or "send.repeat" or "loopback.run" or "sequence.run"
            or "modbus.read" or "modbus.write" or "modbus.scan" or "modbus.poll" or "xmodem.send" or "xmodem.receive"))
        {
            definition.Add(new Option<bool>("--background") { Description = "返回任务标识，在 Host 后台执行" }, false);
            Text(definition, "--request-id", "持久请求标识");
        }
    }

    public RootCommand Root { get; } = new("SerialWorkbench 串口工作台");

    public IReadOnlyList<CommandDefinition> Commands => commands;

    private CommandDefinition Add(string id, string description)
    {
        Command parent = Root;
        var parts = id.Split('.');
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var name = parts[index];
            var group = parent.Subcommands.FirstOrDefault(item => item.Name == name);
            if (group is null)
            {
                group = new Command(name);
                parent.Subcommands.Add(group);
            }

            parent = group;
        }

        var command = new Command(parts[^1], description);
        parent.Subcommands.Add(command);
        var definition = new CommandDefinition(id, command, new Dictionary<string, Option>(globalOptions));
        foreach (var pair in globalDefaults)
        {
            definition.DefaultValues.Add(pair.Key, pair.Value);
        }
        definition.Choices.Add("--output", ["text", "json", "jsonl"]);
        commands.Add(definition);
        return definition;
    }

    private void AddGlobal(string name, string description, string defaultValue, string[]? choices = null)
    {
        var option = new Option<string>(name) { Description = description, DefaultValueFactory = _ => defaultValue, Recursive = true };
        if (choices is not null)
        {
            option.AcceptOnlyFromAmong(choices);
        }

        Root.Options.Add(option);
        globalOptions.Add(name, option);
        globalDefaults.Add(name, defaultValue);
    }

    private static void Text(CommandDefinition definition, string name, string description, string? defaultValue = null, bool required = false, string[]? choices = null)
    {
        var option = new Option<string?>(name) { Description = description, Required = required };
        if (defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
        }

        if (choices is not null)
        {
            option.AcceptOnlyFromAmong(choices);
        }

        definition.Add(option, defaultValue);
        if (choices is not null)
        {
            definition.Choices.Add(name, choices);
        }
    }

    private static void Integer(CommandDefinition definition, string name, string description, int defaultValue, int minimum, int maximum)
    {
        var option = new Option<int>(name) { Description = description, DefaultValueFactory = _ => defaultValue };
        option.Validators.Add(result =>
        {
            if (!result.Implicit && result.Tokens.Count == 1
                && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && (value < minimum || value > maximum))
            {
                result.AddError($"{name} 必须在 {minimum} 到 {maximum} 之间。");
            }
        });
        definition.Add(option, defaultValue, minimum, maximum);
    }

    private static void LongInteger(CommandDefinition definition, string name, string description, long defaultValue, long minimum, long maximum)
    {
        var option = new Option<long>(name) { Description = description, DefaultValueFactory = _ => defaultValue };
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 && long.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && (value < minimum || value > maximum))
            {
                result.AddError($"{name} 必须在 {minimum} 到 {maximum} 之间。");
            }
        });
        definition.Add(option, defaultValue, minimum, maximum);
    }

    private static void SendOptions(CommandDefinition definition)
    {
        Text(definition, "--text", "发送文本");
        Text(definition, "--hex", "发送 HEX");
        Text(definition, "--line-ending", "文本行尾", "none", choices: ["none", "cr", "lf", "crlf"]);
        EnumOption(definition, "--checksum", "追加校验", ChecksumKind.None);
        definition.Command.Validators.Add(result =>
        {
            if ((result.GetResult("--text") is OptionResult { Implicit: false }) == (result.GetResult("--hex") is OptionResult { Implicit: false }))
            {
                result.AddError("发送需要指定 --text 或 --hex 中的一项。");
            }
        });
    }

    private static void GuidOption(CommandDefinition definition, string name, string description, bool required = false) =>
        definition.Add(new Option<Guid?>(name) { Description = description, Required = required });

    private static void EnumOption<T>(CommandDefinition definition, string name, string description, T defaultValue) where T : struct, Enum =>
        definition.Add(new Option<T>(name) { Description = description, DefaultValueFactory = _ => defaultValue }, defaultValue);

    private static void Id(CommandDefinition definition) => GuidOption(definition, "--id", "标识", true);

    private static void Filters(CommandDefinition definition)
    {
        Text(definition, "--direction", "报文方向", "all", choices: ["all", "rx", "tx"]);
        Text(definition, "--source", "来源包含的文字");
    }

    private static void SerialOptions(CommandDefinition definition, bool requirePort = false)
    {
        Text(definition, "--port", "串口名称", required: requirePort);
        if (!requirePort)
        {
            GuidOption(definition, "--connection", "共享连接标识");
            definition.Command.Validators.Add(result =>
            {
                if ((result.GetValue<string?>("--port") is null) == (result.GetValue<Guid?>("--connection") is null))
                {
                    result.AddError("需要指定 --port 或 --connection 中的一项。");
                }
            });
        }

        Integer(definition, "--baud", "波特率", 115200, 1, int.MaxValue);
        Integer(definition, "--data-bits", "数据位", 8, 5, 8);
        EnumOption(definition, "--parity", "校验位", SerialParity.None);
        EnumOption(definition, "--stop-bits", "停止位", SerialStopBits.One);
        EnumOption(definition, "--handshake", "流控", SerialHandshake.None);
        EnumOption(definition, "--role", "设备角色", SerialConnectionRole.Dut);
        Text(definition, "--encoding", "文本编码", "utf-8");
        Text(definition, "--device-id", "设备实例标识");
        foreach (var flag in new[] { "--dtr", "--rts", "--rs485" })
        {
            definition.Add(new Option<bool>(flag), false);
        }

        Integer(definition, "--rts-before", "RS-485 发送前延时，单位毫秒", 0, 0, 60_000);
        Integer(definition, "--rts-after", "RS-485 发送后延时，单位毫秒", 0, 0, 60_000);
        definition.Add(new Option<bool>("--no-reconnect") { Description = "关闭按设备身份自动重连" }, false);
    }

    private static void ModbusOptions(CommandDefinition definition, int defaultFunction, int[] functions)
    {
        SerialOptions(definition);
        Integer(definition, "--slave", "从站地址", 1, 1, 247);
        Integer(definition, "--address", "起始地址", 0, 0, ushort.MaxValue);
        Integer(definition, "--quantity", "读取数量", 1, 1, 2000);
        Integer(definition, "--timeout", "响应超时，单位毫秒", 2000, 1, 600_000);
        var function = new Option<int>("--function") { Description = "功能码", DefaultValueFactory = _ => defaultFunction };
        function.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1
                && int.TryParse(result.Tokens[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && !functions.Contains(value))
            {
                result.AddError($"--function 可用值：{string.Join(", ", functions)}。");
            }
        });
        function.CompletionSources.Add(_ => functions.Select(static item => new System.CommandLine.Completions.CompletionItem(item.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        definition.Add(function, defaultFunction);
        definition.Choices.Add(function.Name, functions.Select(static value => value.ToString(CultureInfo.InvariantCulture)).ToArray());
    }
}

public sealed record CommandDefinition(string Id, Command Command, Dictionary<string, Option> Options)
{
    public Dictionary<string, object?> DefaultValues { get; } = [];

    public Dictionary<string, (long Minimum, long Maximum)> Ranges { get; } = [];

    public Dictionary<string, string[]> Choices { get; } = [];

    public CommandArguments Bind(ParseResult result) => new(this, result);

    internal void Add(Option option, object? defaultValue = null, long? minimum = null, long? maximum = null)
    {
        Command.Options.Add(option);
        Options.Add(option.Name, option);
        DefaultValues.Add(option.Name, defaultValue);
        if (minimum is { } min && maximum is { } max)
        {
            Ranges.Add(option.Name, (min, max));
        }
    }
}
