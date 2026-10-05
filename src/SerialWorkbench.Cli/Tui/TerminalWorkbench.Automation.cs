using System.Collections.ObjectModel;
using System.Text;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Protocols;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly TextField sequenceName = new() { Id = "sequence-name", X = 12, Width = Dim.Fill(), Text = "新建序列" };
    private readonly TableView sequenceTable = new() { Id = "sequence-steps", Y = 4, Height = Dim.Percent(35), Width = Dim.Fill(), FullRowSelect = true, MultiSelect = false };
    private readonly List<SerialSequenceStep> sequenceSteps = [];
    private readonly TextField stepData = new() { Id = "sequence-data", X = 20, Width = Dim.Fill(1) };
    private readonly DropDownList stepFormat = new()
    {
        Id = "sequence-format",
        X = 20,
        Y = 2,
        Width = 16,
        ReadOnly = true,
        Text = "HEX",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["HEX", "文本"]))
    };
    private readonly NumericUpDown<int> stepDelay = new() { CanEdit = true, X = 20, Y = 4, Value = 0, Width = 16 };
    private readonly NumericUpDown<int> stepRepeat = new() { CanEdit = true, X = 20, Y = 6, Value = 1, Width = 16 };
    private readonly NumericUpDown<int> stepWait = new() { CanEdit = true, X = 20, Y = 8, Value = 0, Width = 16 };
    private readonly TextField stepResponse = new() { Id = "sequence-response", X = 20, Y = 10, Width = Dim.Fill(1) };
    private readonly NumericUpDown<int> stepTimeout = new() { CanEdit = true, X = 20, Y = 12, Value = 2000, Width = 16 };
    private readonly NumericUpDown<int> stepRetries = new() { CanEdit = true, X = 20, Y = 14, Value = 0, Width = 16 };
    private bool updatingSequence;

    private View BuildAutomation()
    {
        var view = new View { Title = "自动化", Width = Dim.Fill(), Height = Dim.Fill() };
        var load = Button("加载", () => RunUiAsync(async () =>
        {
            var path = ChooseFile(false);
            if (path is null)
            {
                return;
            }
            var definition = SerialSequenceCodec.Deserialize(await File.ReadAllTextAsync(path, lifetime.Token).ConfigureAwait(false));
            await InvokeUiAsync(() => ApplySequence(definition)).ConfigureAwait(false);
        }));
        load.Y = 2;
        var save = Button("保存", () => RunUiAsync(async () =>
        {
            var definition = ReadSequence();
            var path = ChooseFile(true);
            if (path is not null)
            {
                await File.WriteAllTextAsync(path, SerialSequenceCodec.Serialize(definition), new UTF8Encoding(false), lifetime.Token).ConfigureAwait(false);
                app.Invoke(() => message.Text = $"已保存：{path}");
            }
        }));
        save.X = Pos.Right(load) + 1;
        save.Y = 2;
        var run = Button("运行", () => RunUiAsync(() => StartTaskAsync(OperationJson.Create("sequence.run", RequiredConnection(), ReadSequence()))));
        run.X = Pos.Right(save) + 1;
        run.Y = 2;
        var add = Button("新增步骤", () => RunUiAsync(() =>
        {
            sequenceSteps.Add(ReadSequenceStep());
            RefreshSequence(sequenceSteps.Count - 1);
            return Task.CompletedTask;
        }));
        add.X = 0;
        add.Y = 3;
        var update = Button("更新步骤", () => RunUiAsync(() =>
        {
            if (SequenceIndex() is { } index)
            {
                sequenceSteps[index] = ReadSequenceStep();
                RefreshSequence(index);
            }
            return Task.CompletedTask;
        }));
        update.X = Pos.Right(add) + 1;
        update.Y = 3;
        var delete = Button("删除步骤", () =>
        {
            if (SequenceIndex() is { } index)
            {
                sequenceSteps.RemoveAt(index);
                RefreshSequence(Math.Min(index, sequenceSteps.Count - 1));
            }
            return Task.CompletedTask;
        });
        delete.X = Pos.Right(update) + 1;
        delete.Y = 3;
        var up = Button("上移", () => { MoveSequenceStep(-1); return Task.CompletedTask; });
        up.X = Pos.Right(run) + 1;
        up.Y = 2;
        var down = Button("下移", () => { MoveSequenceStep(1); return Task.CompletedTask; });
        down.X = Pos.Right(up) + 1;
        down.Y = 2;
        var edit = new FrameView { Title = "步骤参数 · 修改后更新步骤", Y = Pos.Bottom(sequenceTable), Width = Dim.Fill(), Height = Dim.Fill(), ViewportSettings = ViewportSettingsFlags.HasScrollBars };
        edit.Add(new Label { Text = "发送数据" }, stepData, new Label { Text = "格式", Y = 2 }, stepFormat,
            new Label { Text = "发送间隔 ms", Y = 4 }, stepDelay, new Label { Text = "重复次数", Y = 6 }, stepRepeat,
            new Label { Text = "发送后等待 ms", Y = 8 }, stepWait, new Label { Text = "匹配响应 HEX", Y = 10 }, stepResponse,
            new Label { Text = "响应超时 ms", Y = 12 }, stepTimeout, new Label { Text = "响应重试次数", Y = 14 }, stepRetries);
        edit.SetContentSize(new System.Drawing.Size(80, 16));
        sequenceTable.ValueChanged += (_, _) =>
        {
            if (!updatingSequence && SequenceIndex() is { } index)
            {
                LoadSequenceStep(sequenceSteps[index]);
            }
        };
        view.Add(new Label { Text = "序列名称" }, sequenceName, load, save, run, add, update, delete, up, down, sequenceTable, edit);
        RefreshSequence(-1);
        return view;
    }

    internal SerialSequenceStep ReadSequenceStep()
    {
        var bytes = stepFormat.Text == "HEX" ? HexCodec.Parse(stepData.Text) : Encoding.GetEncoding(encoding.Text).GetBytes(stepData.Text);
        var response = string.IsNullOrWhiteSpace(stepResponse.Text) ? null : HexCodec.Format(HexCodec.Parse(stepResponse.Text));
        if (stepRepeat.Value is < 1 or > 10_000 || stepDelay.Value is < 0 or > 600_000 || stepWait.Value is < 0 or > 600_000
            || stepTimeout.Value is < 0 or > 600_000 || stepRetries.Value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(stepRepeat), "步骤次数、延时或重试参数超出范围。");
        }
        return new SerialSequenceStep(bytes, stepFormat.Text == "HEX" ? "hex" : "text", stepDelay.Value, stepRepeat.Value, stepWait.Value,
            response, stepTimeout.Value, stepRetries.Value);
    }

    internal SerialSequenceDefinition ReadSequence()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceName.Text);
        if (sequenceSteps.Count == 0)
        {
            throw new InvalidOperationException("请先新增序列步骤。");
        }
        return new SerialSequenceDefinition(sequenceName.Text, sequenceSteps.ToArray());
    }

    internal void ApplySequence(SerialSequenceDefinition definition)
    {
        sequenceName.Text = definition.Name;
        sequenceSteps.Clear();
        sequenceSteps.AddRange(definition.Steps);
        RefreshSequence(sequenceSteps.Count > 0 ? 0 : -1);
    }

    private int? SequenceIndex() => sequenceTable.Value is { } selected && selected.SelectedCell.Y >= 0 && selected.SelectedCell.Y < sequenceSteps.Count
        ? selected.SelectedCell.Y : null;

    private void MoveSequenceStep(int offset)
    {
        if (SequenceIndex() is { } index && index + offset >= 0 && index + offset < sequenceSteps.Count)
        {
            (sequenceSteps[index], sequenceSteps[index + offset]) = (sequenceSteps[index + offset], sequenceSteps[index]);
            RefreshSequence(index + offset);
        }
    }

    private void RefreshSequence(int index)
    {
        updatingSequence = true;
        sequenceTable.Table = new EnumerableTableSource<SerialSequenceStep>(sequenceSteps, new Dictionary<string, Func<SerialSequenceStep, object>>
        {
            ["HEX"] = item => HexCodec.Format(item.Data),
            ["次数"] = item => item.RepeatCount,
            ["间隔 ms"] = item => item.DelayMilliseconds,
            ["等待 ms"] = item => item.WaitMilliseconds,
            ["响应"] = item => item.ResponseHex ?? "",
            ["超时 ms"] = item => item.ResponseTimeoutMilliseconds,
            ["重试"] = item => item.RetryCount,
        });
        if (index >= 0 && index < sequenceSteps.Count)
        {
            sequenceTable.Value = new TableSelection(new System.Drawing.Point(0, index));
            LoadSequenceStep(sequenceSteps[index]);
        }
        updatingSequence = false;
    }

    private void LoadSequenceStep(SerialSequenceStep step)
    {
        stepFormat.Text = step.Format.Equals("text", StringComparison.OrdinalIgnoreCase) ? "文本" : "HEX";
        stepData.Text = stepFormat.Text == "文本" ? Encoding.GetEncoding(encoding.Text).GetString(step.Data) : HexCodec.Format(step.Data);
        stepDelay.Value = step.DelayMilliseconds;
        stepRepeat.Value = step.RepeatCount;
        stepWait.Value = step.WaitMilliseconds;
        stepResponse.Text = step.ResponseHex ?? "";
        stepTimeout.Value = step.ResponseTimeoutMilliseconds;
        stepRetries.Value = step.RetryCount;
    }
}
