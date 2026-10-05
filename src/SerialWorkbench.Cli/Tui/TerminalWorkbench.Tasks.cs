using System.Globalization;
using System.Text.Json;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly TableView taskTable = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Fill(4), FullRowSelect = true };
    private readonly ProgressBar progress = new() { Y = Pos.AnchorEnd(3), Width = Dim.Fill() };
    private readonly Label progressText = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
    private OperationSnapshot[] displayedTasks = [];
    private readonly TableView modbusResults = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
    private readonly Label modbusSummary = new() { Width = Dim.Fill(), Text = "选择参数并执行事务" };
    private readonly List<ModbusSample> modbusSamples = [];
    private Guid? modbusOperationId;
    private long modbusRevision;

    private View BuildTasks()
    {
        var view = new View { Title = "任务", Width = Dim.Fill(), Height = Dim.Fill() };
        var refresh = Button("刷新", () => RunUiAsync(RefreshTasksAsync));
        var cancel = Button("取消任务", () => RunUiAsync(CancelSelectedTaskAsync));
        cancel.X = Pos.Right(refresh) + 1;
        var result = Button("查看结果", () => RunUiAsync(ShowTaskResultAsync));
        result.X = Pos.Right(cancel) + 1;
        view.Add(refresh, cancel, result, taskTable, progress, progressText);
        return view;
    }

    private View BuildModbus()
    {
        var view = new View { Title = "Modbus", Width = Dim.Fill(), Height = Dim.Fill() };
        var parameters = new FrameView { Title = "事务参数", Width = 43, Height = Dim.Fill(), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        var slave = Number(parameters, "从站", 0, 1);
        var function = Number(parameters, "功能码", 2, 3);
        var address = Number(parameters, "地址", 4, 0);
        var quantity = Number(parameters, "数量", 6, 1);
        var value = Number(parameters, "单个写入值", 8, 0);
        var values = Field(parameters, "多个值", 10, "1,2,3");
        var count = Number(parameters, "轮询次数", 12, 10);
        var interval = Number(parameters, "间隔 ms", 14, 1000);
        var timeout = Number(parameters, "超时 ms", 16, 2000);
        var first = Number(parameters, "扫描起始从站", 18, 1);
        var last = Number(parameters, "扫描结束从站", 20, 247);
        var execute = Button("执行事务", () => RunUiAsync(async () =>
        {
            var id = RequiredConnection();
            var slaveAddress = checked((byte)slave.Value);
            var code = checked((byte)function.Value);
            var register = checked((ushort)address.Value);
            var data = values.Text.Split([',', ' ', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var frame = code switch
            {
                1 or 2 or 3 or 4 => ModbusRtuCodec.BuildReadRequest(slaveAddress, code, register, checked((ushort)quantity.Value)),
                5 => ModbusRtuCodec.BuildWriteSingleCoil(slaveAddress, register, value.Value switch { 0 => false, 1 => true, _ => throw new ArgumentException("线圈值需要 0 或 1。") }),
                6 => ModbusRtuCodec.BuildWriteSingleRegister(slaveAddress, register, checked((ushort)value.Value)),
                15 => ModbusRtuCodec.BuildWriteMultipleCoils(slaveAddress, register, data.Select(static item => item switch { "0" => false, "1" => true, _ => throw new FormatException("线圈值需要 0 或 1。") }).ToArray()),
                16 => ModbusRtuCodec.BuildWriteMultipleRegisters(slaveAddress, register, data.Select(static item => ushort.Parse(item, CultureInfo.InvariantCulture)).ToArray()),
                17 => ModbusRtuCodec.BuildReportServerIdRequest(slaveAddress),
                _ => throw new ArgumentException("支持功能码 1、2、3、4、5、6、15、16、17。"),
            };
            await StartTaskAsync(OperationJson.Create(code is 5 or 6 or 15 or 16 ? "modbus.write" : "modbus.read", id,
                new ModbusTransactionRequest(id, frame, slaveAddress, code, timeout.Value))).ConfigureAwait(false);
        }));
        execute.Y = 22;
        var poll = Button("开始轮询", () => RunUiAsync(() =>
        {
            var id = RequiredConnection();
            return StartTaskAsync(OperationJson.Create("modbus.poll", id, new ModbusPollRequest(id, checked((byte)slave.Value),
                checked((byte)function.Value), checked((ushort)address.Value), checked((ushort)quantity.Value), count.Value, interval.Value, timeout.Value)));
        }));
        poll.X = 0;
        poll.Y = 24;
        var scan = Button("扫描从站", () => RunUiAsync(() =>
        {
            var id = RequiredConnection();
            return StartTaskAsync(OperationJson.Create("modbus.scan", id, new ModbusScanRequest(id, checked((byte)first.Value), checked((byte)last.Value),
                checked((ushort)address.Value), timeout.Value, interval.Value)));
        }));
        scan.X = Pos.Right(poll) + 1;
        scan.Y = 24;
        parameters.Add(execute, poll, scan);
        parameters.SetContentSize(new System.Drawing.Size(40, 26));
        EnableFormScrolling(parameters);
        var results = new FrameView { Title = "结果 · Enter 查看详情", X = Pos.Right(parameters), Width = Dim.Fill(), Height = Dim.Fill() };
        var copy = Button("复制结果", () => { app.Clipboard?.TrySetClipboardData(JsonSerializer.Serialize(modbusSamples, MachineOutput.DocumentOptions)); return Task.CompletedTask; });
        copy.Y = 1;
        var stop = Button("停止", () => RunUiAsync(async () =>
        {
            if (modbusOperationId is { } id)
            {
                await client.CancelOperationAsync(id, lifetime.Token).ConfigureAwait(false);
            }
        }));
        stop.Y = 1;
        stop.X = Pos.Right(copy) + 1;
        modbusResults.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (modbusResults.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < modbusSamples.Count)
            {
                ShowText("Modbus 响应", JsonSerializer.Serialize(modbusSamples[selection.SelectedCell.Y], MachineOutput.DocumentOptions));
            }
        };
        results.Add(modbusSummary, copy, stop, modbusResults);
        view.Add(parameters, results);
        return view;
    }

    private View BuildTransfers()
    {
        var page = new View { Title = "文件与回环", Width = Dim.Fill(), Height = Dim.Fill() };
        var view = new View { CanFocus = true, Width = Dim.Fill(), Height = Dim.Fill(), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        var path = Field(view, "文件路径", 0, "");
        var browse = Button("选择文件", () =>
        {
            path.Text = ChooseFile(false) ?? path.Text;
            return Task.CompletedTask;
        });
        browse.Y = 2;
        var send = Button("XMODEM 发送", () => RunUiAsync(async () =>
        {
            var id = RequiredConnection();
            var bytes = await File.ReadAllBytesAsync(path.Text, lifetime.Token).ConfigureAwait(false);
            await StartTaskAsync(OperationJson.Create("xmodem.send", id, bytes)).ConfigureAwait(false);
        }));
        send.Y = 4;
        var receive = Button("XMODEM 接收", () => RunUiAsync(async () =>
        {
            var destination = ChooseFile(true);
            if (destination is null)
            {
                return;
            }

            var id = RequiredConnection();
            var request = OperationJson.Create("xmodem.receive", id, new XmodemReceiveRequest(Path.GetFullPath(destination)));
            await StartTaskAsync(request).ConfigureAwait(false);
        }));
        receive.Y = 4;
        receive.X = Pos.Right(send) + 1;
        var length = Number(view, "回环长度", 9, 4096);
        var iterations = Number(view, "回环次数", 11, 1);
        var timeout = Number(view, "回环超时 ms", 13, 5000);
        var pattern = new OptionSelector<LoopbackPattern> { X = 20, Y = 15, Orientation = Orientation.Horizontal };
        var seed = Number(view, "随机种子", 17, 0x534257);
        var loopback = Button("运行回环", () => RunUiAsync(() =>
        {
            var id = RequiredConnection();
            return StartTaskAsync(OperationJson.Create("loopback.run", id, new LoopbackRequest(id, length.Value, iterations.Value, timeout.Value,
                pattern.Value ?? LoopbackPattern.Incrementing, seed.Value)));
        }));
        loopback.Y = 19;
        view.Add(browse, send, receive, new Label { Text = "回环模式", Y = 15 }, pattern, loopback);
        view.SetContentHeight(21);
        EnableFormScrolling(view);
        page.Add(view);
        return page;
    }

    private async Task StartTaskAsync(OperationRequest request, Action<OperationSnapshot>? accepted = null)
    {
        var background = backgroundTasks.Value == CheckState.Checked;
        lifetime.Token.ThrowIfCancellationRequested();
        var started = await client.StartOperationAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (started.Operation is not { } operation)
        {
            throw new HostOperationException(started.Error ?? new WorkbenchError("OPERATION_FAILED", "任务未接受。"));
        }

        try
        {
            await InvokeUiAsync(() =>
            {
                message.Text = $"任务已启动：{operation.Id}";
                accepted?.Invoke(operation);
                if (operation.Request.Command.StartsWith("modbus.", StringComparison.Ordinal))
                {
                    modbusOperationId = operation.Id;
                    modbusRevision = 0;
                    modbusSamples.Clear();
                    RefreshModbusResults();
                }
                ShowProgress(operation);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (!background)
            {
                await client.CancelOperationAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        if (!background)
        {
            await client.WaitOperationAsync<JsonElement>(operation, lifetime.Token, item =>
            {
                operation = item;
                app.Invoke(() => ShowProgress(item));
            }).ConfigureAwait(false);
            app.Invoke(() => message.Text = operation.Error?.Message ?? $"{operation.Request.Command} · {operation.State}");
        }
        await RefreshTasksAsync().ConfigureAwait(false);
    }

    private async Task RefreshTasksAsync()
    {
        var listed = await client.ListOperationsAsync(lifetime.Token).ConfigureAwait(false);
        if (modbusOperationId is { } id && listed.FirstOrDefault(item => item.Id == id) is { } current && current.Revision != modbusRevision)
        {
            var batch = await client.ReadOperationProgressAsync(new OperationProgressQuery(id, modbusRevision), lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() =>
            {
                if (modbusOperationId != id || batch.NextRevision < modbusRevision)
                {
                    return;
                }
                foreach (var update in batch.Updates)
                {
                    if (update.Progress.ItemJson is not { } itemJson)
                    {
                        continue;
                    }
                    var sample = OperationJson.Read<ModbusSample>(itemJson);
                    if (!modbusSamples.Any(item => item.Index == sample.Index))
                    {
                        modbusSamples.Add(sample);
                    }
                }
                if (batch.Operation.ResultJson is { } json)
                {
                    if (batch.Operation.Request.Command is "modbus.scan" or "modbus.poll")
                    {
                        modbusSamples.Clear();
                        modbusSamples.AddRange(OperationJson.Read<ModbusBatchResult>(json).Samples);
                    }
                    else if (modbusSamples.Count == 0)
                    {
                        var request = OperationJson.Read<ModbusTransactionRequest>(batch.Operation.Request.ParametersJson);
                        modbusSamples.Add(new ModbusSample(1, request.SlaveAddress, batch.Operation.UpdatedUtc, OperationJson.Read<ModbusTransactionResult>(json)));
                    }
                }
                modbusRevision = batch.NextRevision;
                RefreshModbusResults();
            }).ConfigureAwait(false);
        }
        app.Invoke(() =>
        {
            var selected = taskTable.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < displayedTasks.Length
                ? displayedTasks[selection.SelectedCell.Y].Id : (Guid?)null;
            var viewport = taskTable.Viewport;
            displayedTasks = listed.ToArray();
            taskTable.Table = new EnumerableTableSource<OperationSnapshot>(displayedTasks, new Dictionary<string, Func<OperationSnapshot, object>>
            {
                ["任务"] = item => item.Id,
                ["命令"] = item => item.Request.Command,
                ["状态"] = item => item.State,
                ["进度"] = item => item.Progress?.Completed.ToString(CultureInfo.InvariantCulture) ?? "",
                ["结果"] = item => item.Error?.Code ?? "",
            });
            var index = Array.FindIndex(displayedTasks, item => item.Id == selected);
            if (index >= 0)
            {
                taskTable.Value = new TableSelection(new System.Drawing.Point(0, index));
                taskTable.Viewport = viewport;
            }
            if ((displayedTasks.FirstOrDefault(item => item.Id == selected) ?? displayedTasks.FirstOrDefault()) is { } current)
            {
                ShowProgress(current);
            }
        });
    }

    private void ShowProgress(OperationSnapshot operation)
    {
        var completed = operation.Progress?.Completed ?? 0;
        var total = operation.Progress?.Total;
        progress.Fraction = total is > 0 ? (float)Math.Clamp((double)completed / total.Value, 0, 1) : 0;
        progressText.Text = $"{operation.Request.Command} · {operation.State} · {completed}/{total?.ToString(CultureInfo.InvariantCulture) ?? "?"} {operation.Progress?.Unit}";
    }

    private void RefreshModbusResults()
    {
        var selection = modbusResults.Value;
        var viewport = modbusResults.Viewport;
        var succeeded = modbusSamples.Count(static sample => sample.Result.Success);
        modbusSummary.Text = $"{modbusSamples.Count} 次 · 成功 {succeeded} · 失败 {modbusSamples.Count - succeeded}";
        modbusResults.Table = new EnumerableTableSource<ModbusSample>(modbusSamples, new Dictionary<string, Func<ModbusSample, object>>
        {
            ["序号"] = item => item.Index,
            ["从站"] = item => item.SlaveAddress,
            ["功能"] = item => item.Result.FunctionCode,
            ["数值"] = item => item.Result.Bits is { } bits ? string.Join(',', bits.Select(static bit => bit ? 1 : 0))
                : string.Join(',', item.Result.Registers),
            ["ms"] = item => item.Result.Duration.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
            ["状态"] = item => item.Result.Success ? "成功" : item.Result.ErrorCode ?? "失败",
        });
        if (selection is not null)
        {
            modbusResults.Value = selection;
            modbusResults.Viewport = viewport;
        }
    }

    private async Task CancelSelectedTaskAsync()
    {
        if (taskTable.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < displayedTasks.Length)
        {
            await client.CancelOperationAsync(displayedTasks[selection.SelectedCell.Y].Id, lifetime.Token).ConfigureAwait(false);
            await RefreshTasksAsync().ConfigureAwait(false);
        }
    }

    private async Task ShowTaskResultAsync()
    {
        if (taskTable.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < displayedTasks.Length)
        {
            var operation = await client.ReadOperationAsync(new OperationQuery(displayedTasks[selection.SelectedCell.Y].Id), lifetime.Token).ConfigureAwait(false);
            app.Invoke(() =>
            {
                var text = operation.ResultJson is { } json ? JsonSerializer.Serialize(OperationJson.Read<JsonElement>(json), MachineOutput.DocumentOptions)
                    : operation.Error?.Message ?? operation.State.ToString();
                ShowText("任务结果", text);
            });
        }
    }

    private Guid RequiredConnection() => connectionId ?? throw new InvalidOperationException("请先选择连接。");

    private string? ChooseFile(bool save)
    {
        if (save)
        {
            using var dialog = new SaveDialog { Title = "保存文件", Path = Environment.CurrentDirectory };
            app.Run(dialog);
            return dialog.Canceled ? null : dialog.Path;
        }

        using var open = new OpenDialog { Title = "选择文件", Path = Environment.CurrentDirectory, AllowsMultipleSelection = false, OpenMode = OpenMode.File };
        app.Run(open);
        return open.Canceled ? null : open.Path;
    }

    private static NumericUpDown<int> Number(View view, string label, int row, int value)
    {
        var input = new NumericUpDown<int> { CanEdit = true, X = 20, Y = row, Value = value, Width = 20 };
        view.Add(new Label { Text = label, Y = row }, input);
        return input;
    }

    private static TextField Field(View view, string label, int row, string value)
    {
        var input = new TextField { X = 20, Y = row, Text = value, Width = Dim.Fill(1) };
        view.Add(new Label { Text = label, Y = row }, input);
        return input;
    }
}
