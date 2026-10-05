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
    private readonly ListView taskResult = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private OperationSnapshot[] displayedTasks = [];

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
        var view = new View { Title = "Modbus", Width = Dim.Fill(), Height = Dim.Fill(), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        var slave = Number(view, "从站", 0, 1);
        var function = Number(view, "功能码", 2, 3);
        var address = Number(view, "地址", 4, 0);
        var quantity = Number(view, "数量", 6, 1);
        var value = Number(view, "单个写入值", 8, 0);
        var values = Field(view, "多个值", 10, "1,2,3");
        var count = Number(view, "轮询次数", 12, 10);
        var interval = Number(view, "间隔 ms", 14, 1000);
        var timeout = Number(view, "超时 ms", 16, 2000);
        var first = Number(view, "扫描起始从站", 18, 1);
        var last = Number(view, "扫描结束从站", 20, 247);
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
                5 => ModbusRtuCodec.BuildWriteSingleCoil(slaveAddress, register, value.Value != 0),
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
        poll.X = Pos.Right(execute) + 1;
        poll.Y = 22;
        var scan = Button("扫描从站", () => RunUiAsync(() =>
        {
            var id = RequiredConnection();
            return StartTaskAsync(OperationJson.Create("modbus.scan", id, new ModbusScanRequest(id, checked((byte)first.Value), checked((byte)last.Value),
                checked((ushort)address.Value), timeout.Value, interval.Value)));
        }));
        scan.X = Pos.Right(poll) + 1;
        scan.Y = 22;
        view.Add(execute, poll, scan);
        return view;
    }

    private View BuildTransfers()
    {
        var view = new View { Title = "文件与自动化", Width = Dim.Fill(), Height = Dim.Fill() };
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
        var sequence = Button("运行序列 JSON", () => RunUiAsync(async () =>
        {
            var id = RequiredConnection();
            var definition = SerialSequenceCodec.Deserialize(await File.ReadAllTextAsync(path.Text, lifetime.Token).ConfigureAwait(false));
            await StartTaskAsync(OperationJson.Create("sequence.run", id, definition)).ConfigureAwait(false);
        }));
        sequence.Y = 6;
        var length = Number(view, "回环长度", 9, 4096);
        var iterations = Number(view, "回环次数", 11, 1);
        var timeout = Number(view, "回环超时 ms", 13, 5000);
        var loopback = Button("运行回环", () => RunUiAsync(() =>
        {
            var id = RequiredConnection();
            return StartTaskAsync(OperationJson.Create("loopback.run", id, new LoopbackRequest(id, length.Value, iterations.Value, timeout.Value)));
        }));
        loopback.Y = 15;
        view.Add(browse, send, receive, sequence, loopback);
        return view;
    }

    private async Task StartTaskAsync(OperationRequest request)
    {
        var background = backgroundTasks.Value == CheckState.Checked;
        lifetime.Token.ThrowIfCancellationRequested();
        var started = await client.StartOperationAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (started.Operation is not { } operation)
        {
            throw new HostOperationException(started.Error ?? new WorkbenchError("OPERATION_FAILED", "任务未接受。"));
        }

        app.Invoke(() =>
        {
            message.Text = $"任务已启动：{operation.Id}";
            ShowProgress(operation);
        });
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
            if (displayedTasks.FirstOrDefault(static item => item.State == OperationState.Running) is { } current)
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
                using var dialog = new Dialog { Title = "任务结果", Width = Dim.Percent(90), Height = Dim.Percent(85) };
                var text = operation.ResultJson is { } json ? JsonSerializer.Serialize(OperationJson.Read<JsonElement>(json), MachineOutput.DocumentOptions)
                    : operation.Error?.Message ?? operation.State.ToString();
                taskResult.SetSource(new System.Collections.ObjectModel.ObservableCollection<string>(text.Split('\n')));
                dialog.Add(taskResult);
                dialog.AddButton(new Button { Text = "关闭" });
                app.Run(dialog);
                dialog.Remove(taskResult);
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

        using var open = new OpenDialog { Title = "选择文件", Path = Environment.CurrentDirectory, AllowsMultipleSelection = false };
        app.Run(open);
        return open.Canceled ? null : open.Path;
    }

    private static NumericUpDown<int> Number(View view, string label, int row, int value)
    {
        var input = new NumericUpDown<int> { X = 20, Y = row, Value = value, Width = 20 };
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
