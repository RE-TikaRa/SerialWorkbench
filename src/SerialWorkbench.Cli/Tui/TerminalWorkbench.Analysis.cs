using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Modbus;
using SerialWorkbench.Protocols;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SerialWorkbench.Cli.Tui;

public sealed partial class TerminalWorkbench
{
    private readonly TableView sessions = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Percent(30), FullRowSelect = true, MultiSelect = false };
    private readonly TableView sessionEvents = new() { Width = Dim.Fill(), Height = Dim.Fill(1), FullRowSelect = true };
    private readonly TextField sessionFilter = new() { Id = "session-hex", Y = 3, X = 8, Width = Dim.Fill(16) };
    private readonly DropDownList sessionDirection = new()
    {
        Id = "session-direction",
        X = 0,
        Y = 2,
        Width = 10,
        ReadOnly = true,
        Text = "全部",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["全部", "RX", "TX"]))
    };
    private readonly TextField sessionSource = new() { Id = "session-source", X = 18, Y = 2, Width = 14 };
    private readonly TextField sessionConnection = new() { Id = "session-connection", X = 41, Y = 2, Width = Dim.Fill() };
    private readonly DropDownList sessionExportFormat = new()
    {
        X = 0,
        Y = 4,
        Width = 12,
        ReadOnly = true,
        Text = "csv",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["csv", "jsonl", "text", "hex", "binary"]))
    };
    private readonly NumericUpDown<double> replayRate = new() { X = 46, Y = 4, Width = 10, Value = 1 };
    private readonly Label sessionCount = new() { Y = Pos.AnchorEnd(), Width = Dim.Fill() };
    private bool updatingSessions;
    private bool replayPaused;
    private TaskCompletionSource replayResumed = CompletedReplaySignal();
    private SessionDescriptor[] displayedSessions = [];
    private Guid? selectedSession;
    private long sessionSequence;
    private readonly List<SerialTrafficEvent> displayedSessionEvents = [];
    private readonly SemaphoreSlim sessionReadGate = new(1, 1);
    private long sessionRevision;
    private CancellationTokenSource? replay;
    private SerialWorkbench.Application.TrafficBuffer? replayBuffer;
    private Task replayTask = Task.CompletedTask;
    private readonly GraphView graph = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly WaveformParser waveformParser = new();
    private readonly List<List<PointF>> wavePoints = [];
    private readonly DropDownList waveformMode = new()
    {
        Width = 24,
        ReadOnly = true,
        Text = "CSV 文本",
        Source = new ListWrapper<string>(new ObservableCollection<string>(["CSV 文本", "二进制帧"]))
    };
    private readonly DropDownList waveformType = new()
    {
        X = 26,
        Width = 24,
        ReadOnly = true,
        Text = "Int16LittleEndian",
        Source = new ListWrapper<string>(new ObservableCollection<string>(Enum.GetNames<WaveformSampleType>()))
    };
    private readonly NumericUpDown<int> waveformLength = new() { X = 60, Y = 0, Value = 8, Width = 12 };
    private int sampleIndex;
    private readonly CheckBox waveformPaused = new() { X = 0, Y = 2, Text = "暂停波形" };
    private readonly CheckBox waveformFollow = new() { X = 16, Y = 2, Text = "自动缩放", Value = CheckState.Checked };
    private readonly Label waveformStatus = new() { Y = 3, Width = Dim.Fill() };
    private (Guid ConnectionId, Guid? SegmentId)? waveformStream;

    private View BuildSessions()
    {
        var view = new View { Title = "会话", Width = Dim.Fill(), Height = Dim.Fill() };
        var refresh = Button("刷新", () => RunUiAsync(RefreshSessionsAsync));
        var load = Button("加载更多", () => RunUiAsync(ReadSessionAsync));
        load.X = Pos.Right(refresh) + 1;
        var export = Button("导出", () => RunUiAsync(ExportSessionAsync));
        export.X = Pos.Right(load) + 1;
        var play = Button("回放", () => RunUiAsync(ReplaySessionAsync));
        play.X = Pos.Right(export) + 1;
        var stop = Button("停止回放", () =>
        {
            replay?.Cancel();
            return Task.CompletedTask;
        });
        stop.X = 35;
        stop.Y = 1;
        var pause = Button("暂停／继续", () =>
        {
            replayPaused = !replayPaused;
            if (replayPaused)
            {
                replayResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                replayResumed.TrySetResult();
            }
            return Task.CompletedTask;
        });
        pause.Y = 1;
        pause.X = 18;
        var delete = Button("删除会话", () => RunUiAsync(async () =>
        {
            var id = selectedSession ?? throw new InvalidOperationException("请选择会话。");
            if (MessageBox.Query(app, "删除会话", "删除所选会话文件？", "删除", "取消") == 0)
            {
                await client.DeleteSessionAsync(id, lifetime.Token).ConfigureAwait(false);
                await RefreshSessionsAsync().ConfigureAwait(false);
            }
        }));
        delete.Y = 1;
        var history = Button("回环记录", () => RunUiAsync(async () =>
        {
            var id = selectedSession ?? throw new InvalidOperationException("请选择会话。");
            var results = await client.ReadLoopbackResultsAsync(id, lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() => ShowText("回环记录", JsonSerializer.Serialize(results, MachineOutput.DocumentOptions))).ConfigureAwait(false);
        }));
        history.X = Pos.Right(delete) + 1;
        history.Y = 1;
        sessions.ValueChanged += (_, args) =>
        {
            if (!updatingSessions && args.NewValue is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < displayedSessions.Length)
            {
                var id = displayedSessions[selection.SelectedCell.Y].Id;
                if (selectedSession != id)
                {
                    selectedSession = id;
                    sessionRevision++;
                    sessionSequence = 0;
                    displayedSessionEvents.Clear();
                    sessionEvents.Table = null;
                    _ = RunUiAsync(ReadSessionAsync);
                }
            }
        };
        var applyFilter = Button("筛选与回放", () => RunUiAsync(ConfigureSessionAsync));
        applyFilter.X = Pos.Right(play) + 1;
        sessionEvents.Y = Pos.Bottom(sessions);
        history.X = 0;
        history.Y = 1;
        delete.X = Pos.Right(stop) + 1;
        view.Add(refresh, load, export, play, stop, delete, history, pause, sessions, sessionEvents, applyFilter, sessionCount);
        sessionEvents.Accepting += (_, args) =>
        {
            args.Handled = true;
            if (sessionEvents.Value is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < displayedSessionEvents.Count)
            {
                ShowText("会话事件", JsonSerializer.Serialize(displayedSessionEvents[selection.SelectedCell.Y], MachineOutput.DocumentOptions));
            }
        };
        return view;
    }

    private async Task RefreshSessionsAsync()
    {
        var listed = await client.ListSessionsAsync(lifetime.Token).ConfigureAwait(false);
        app.Invoke(() =>
        {
            var selected = selectedSession;
            updatingSessions = true;
            displayedSessions = listed.ToArray();
            sessions.Table = new EnumerableTableSource<SessionDescriptor>(displayedSessions, new Dictionary<string, Func<SessionDescriptor, object>>
            {
                ["会话"] = item => item.Id,
                ["开始"] = item => item.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                ["事件"] = item => item.EventCount,
                ["字节"] = item => item.RawByteCount,
                ["文件"] = item => item.Path,
            });
            var index = Array.FindIndex(displayedSessions, item => item.Id == selected);
            sessions.Value = displayedSessions.Length == 0 ? null : new TableSelection(new Point(0, Math.Max(0, index)));
            updatingSessions = false;
            var next = displayedSessions.Length == 0 ? (Guid?)null : displayedSessions[Math.Max(0, index)].Id;
            if (selectedSession != next)
            {
                selectedSession = next;
                sessionRevision++;
                sessionSequence = 0;
                displayedSessionEvents.Clear();
                sessionEvents.Table = null;
            }
            _ = RunUiAsync(ReadSessionAsync);
        });
    }

    private async Task ReadSessionAsync()
    {
        if (selectedSession is not { } id)
        {
            return;
        }

        var revision = sessionRevision;
        var query = CreateSessionQuery(id) with { AfterSequence = sessionSequence };
        await sessionReadGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (revision != sessionRevision || query.AfterSequence != sessionSequence)
            {
                return;
            }
            var events = await client.ReadSessionEventsAsync(query, lifetime.Token).ConfigureAwait(false);
            await InvokeUiAsync(() =>
            {
                if (selectedSession != id || sessionRevision != revision)
                {
                    return;
                }

                displayedSessionEvents.AddRange(events);
                if (events.Count > 0)
                {
                    sessionSequence = events[^1].Sequence;
                }
                var selection = sessionEvents.Value;
                var viewport = sessionEvents.Viewport;
                sessionEvents.Table = new EnumerableTableSource<SerialTrafficEvent>(displayedSessionEvents, new Dictionary<string, Func<SerialTrafficEvent, object>>
                {
                    ["时间"] = item => item.Utc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    ["方向"] = item => item.Direction == SerialDirection.Receive ? "RX" : "TX",
                    ["来源"] = item => item.Source,
                    ["HEX"] = item => HexCodec.Format(item.Data),
                });
                sessionEvents.Value = selection;
                sessionEvents.Viewport = viewport;
                sessionCount.Text = $"已加载 {displayedSessionEvents.Count:N0} 条 · 游标 {sessionSequence}";
            }).ConfigureAwait(false);
        }
        finally
        {
            sessionReadGate.Release();
        }
    }

    private async Task ExportSessionAsync()
    {
        var id = selectedSession ?? throw new InvalidOperationException("请选择会话。");
        var destination = ChooseFile(true);
        if (destination is null)
        {
            return;
        }

        var result = await SessionExporter.ExportAsync(client, CreateSessionQuery(id), destination, sessionExportFormat.Text, lifetime.Token).ConfigureAwait(false);
        app.Invoke(() => message.Text = $"已导出 {result.Bytes:N0} 字节：{destination}");
    }

    private async Task ReplaySessionAsync()
    {
        var id = selectedSession ?? throw new InvalidOperationException("请选择会话。");
        if (replay is not null)
        {
            return;
        }

        using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (replayRate.Value is <= 0 or > 100 || !double.IsFinite(replayRate.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(replayRate), "回放倍率需要大于 0 且不超过 100。");
        }
        var rate = replayRate.Value;
        var query = CreateSessionQuery(id);
        var remaining = displayedSessions.First(item => item.Id == id).EventCount;
        replayPaused = false;
        replayResumed.TrySetResult();
        replay = source;
        replayBuffer = new SerialWorkbench.Application.TrafficBuffer(Encoding.GetEncoding(encoding.Text));
        paused = false;
        ResetWaveform();
        RefreshTraffic();
        tabs.Value = workbenchView;
        message.Text = "正在回放会话";
        replayTask = RunUiAsync(async () =>
        {
            try
            {
                var afterSequence = 0L;
                DateTimeOffset? previous = null;
                while (remaining > 0)
                {
                    var events = await client.ReadSessionEventsAsync(query with { AfterSequence = afterSequence }, source.Token).ConfigureAwait(false);
                    if (events.Count == 0)
                    {
                        break;
                    }
                    foreach (var item in events.Take((int)Math.Min(remaining, events.Count)))
                    {
                        source.Token.ThrowIfCancellationRequested();
                        if (previous is { } timestamp && item.Utc > timestamp)
                        {
                            await Task.Delay(TimeSpan.FromTicks((long)((item.Utc - timestamp).Ticks / rate)), source.Token).ConfigureAwait(false);
                        }
                        await replayResumed.Task.WaitAsync(source.Token).ConfigureAwait(false);
                        remaining--;
                        previous = item.Utc;
                        afterSequence = item.Sequence;
                        await InvokeUiAsync(() =>
                        {
                            if (!source.IsCancellationRequested && replayBuffer is { } buffer)
                            {
                                buffer.Append([item]);
                                RefreshTraffic();
                                if (item.Direction == SerialDirection.Receive)
                                {
                                    FeedWaveform(item);
                                }
                            }
                        }).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
            }
            finally
            {
                replay = null;
                if (!lifetime.IsCancellationRequested)
                {
                    await InvokeUiAsync(() =>
                    {
                        if (replayBuffer is not null)
                        {
                            message.Text = source.IsCancellationRequested ? "回放已停止 · F7 返回实时" : "会话回放完成 · F7 返回实时";
                        }
                    }).ConfigureAwait(false);
                }
            }
        });
        await replayTask.ConfigureAwait(false);
    }

    private void ResumeLiveTraffic()
    {
        replay?.Cancel();
        replayBuffer = null;
        paused = false;
        ResetWaveform();
        RefreshTraffic();
        message.Text = "实时报文";
    }

    internal SessionEventQuery CreateSessionQuery(Guid id)
    {
        Guid? connection = string.IsNullOrWhiteSpace(sessionConnection.Text) ? null : Guid.Parse(sessionConnection.Text);
        return new SessionEventQuery(id, ConnectionId: connection,
            Direction: sessionDirection.Text switch { "RX" => SerialDirection.Receive, "TX" => SerialDirection.Transmit, _ => null },
            SourceContains: string.IsNullOrWhiteSpace(sessionSource.Text) ? null : sessionSource.Text,
            DataContainsHex: string.IsNullOrWhiteSpace(sessionFilter.Text) ? null : Convert.ToHexString(HexCodec.Parse(sessionFilter.Text)));
    }

    private static TaskCompletionSource CompletedReplaySignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private async Task ConfigureSessionAsync()
    {
        using var dialog = new Dialog { Title = "会话筛选与回放", Width = Dim.Percent(90), Height = 17 };
        var controls = new View[] { sessionDirection, sessionSource, sessionConnection, sessionFilter, sessionExportFormat, replayRate };
        var labels = new[] { "方向", "来源包含", "连接 ID", "HEX 包含", "导出格式", "回放倍率" };
        for (var index = 0; index < controls.Length; index++)
        {
            controls[index].X = 14;
            controls[index].Y = index * 2;
            controls[index].Width = Dim.Fill(1);
            dialog.Add(new Label { Text = labels[index], Y = index * 2 }, controls[index]);
        }
        var applied = false;
        var apply = new Button { Text = "应用" };
        apply.Accepting += (_, _) => applied = true;
        dialog.AddButton(apply);
        dialog.AddButton(new Button { Text = "关闭" });
        app.Run(dialog);
        foreach (var control in controls)
        {
            dialog.Remove(control);
        }
        if (applied)
        {
            sessionSequence = 0;
            sessionRevision++;
            displayedSessionEvents.Clear();
            sessionEvents.Table = null;
            await ReadSessionAsync().ConfigureAwait(false);
        }
    }

    private View BuildProtocol()
    {
        var view = new View { Title = "协议分析", Width = Dim.Fill(), Height = Dim.Fill() };
        var frame = Field(view, "HEX 帧", 0, "01 03 00 00 00 01 84 0A");
        var template = Field(view, "模板路径", 2, "");
        var fields = new TableView { Y = 6, Width = Dim.Fill(), Height = Dim.Fill() };
        var detail = new Label { Y = 5, Width = Dim.Fill() };
        var browse = Button("选择模板", () =>
        {
            template.Text = ChooseFile(false) ?? template.Text;
            return Task.CompletedTask;
        });
        browse.Y = 4;
        var inspect = Button("解析", () => RunUiAsync(async () =>
        {
            var bytes = HexCodec.Parse(frame.Text);
            if (template.Text.Length > 0)
            {
                var definition = ProtocolTemplateCodec.Deserialize(await File.ReadAllTextAsync(template.Text, lifetime.Token).ConfigureAwait(false));
                var result = ProtocolTemplateParser.Inspect(definition, bytes);
                app.Invoke(() =>
                {
                    detail.Text = result.IsValid ? $"{result.Template} · 校验通过" : result.Error ?? "解析失败";
                    fields.Table = new EnumerableTableSource<ProtocolFieldValue>(result.Fields, new Dictionary<string, Func<ProtocolFieldValue, object>>
                    {
                        ["字段"] = item => item.Name,
                        ["类型"] = item => item.Type,
                        ["值"] = item => item.Value,
                        ["HEX"] = item => item.Hex,
                    });
                });
            }
            else
            {
                var result = ModbusRtuCodec.Inspect(bytes);
                app.Invoke(() =>
                {
                    detail.Text = result.IsValid ? $"{result.Kind} · CRC 通过" : result.Error ?? "解析失败";
                    var data = JsonSerializer.SerializeToElement(result, OperationJson.Options).EnumerateObject().Select(static item => new KeyValuePair<string, string>(item.Name, item.Value.ToString())).ToArray();
                    fields.Table = new EnumerableTableSource<KeyValuePair<string, string>>(data, new Dictionary<string, Func<KeyValuePair<string, string>, object>>
                    {
                        ["字段"] = item => item.Key,
                        ["值"] = item => item.Value,
                    });
                });
            }
        }));
        inspect.X = Pos.Right(browse) + 1;
        inspect.Y = 4;
        view.Add(browse, inspect, detail, fields);
        return view;
    }

    private View BuildWaveform()
    {
        var view = new View { Title = "波形", Width = Dim.Fill(), Height = Dim.Fill() };
        graph.MarginLeft = 6;
        graph.MarginBottom = 2;
        waveformMode.ValueChanged += (_, _) => ResetWaveform();
        waveformType.ValueChanged += (_, _) => ResetWaveform();
        waveformLength.ValueChanged += (_, _) => ResetWaveform();
        var clear = Button("清空", () => { ResetWaveform(); return Task.CompletedTask; });
        clear.X = 34;
        clear.Y = 2;
        var export = Button("导出 CSV", () => RunUiAsync(async () =>
        {
            var path = ChooseFile(true);
            if (path is null)
            {
                return;
            }
            var channels = wavePoints.Select(static points => points.ToDictionary(static point => point.X, static point => point.Y)).ToArray();
            await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            await writer.WriteLineAsync("sample," + string.Join(',', Enumerable.Range(1, channels.Length).Select(static index => $"channel{index}"))).ConfigureAwait(false);
            foreach (var sample in channels.SelectMany(static channel => channel.Keys).Distinct().Order())
            {
                var values = channels.Select(channel => channel.TryGetValue(sample, out var value) ? value.ToString("R", CultureInfo.InvariantCulture) : "");
                await writer.WriteLineAsync(sample.ToString("R", CultureInfo.InvariantCulture) + "," + string.Join(',', values)).ConfigureAwait(false);
            }
            app.Invoke(() => message.Text = $"已导出波形：{path}");
        }));
        export.X = Pos.Right(clear) + 1;
        export.Y = 2;
        graph.Y = 4;
        view.Add(waveformMode, waveformType, new Label { Text = "帧长", X = 52 }, waveformLength, waveformPaused, waveformFollow, clear, export, waveformStatus, graph);
        return view;
    }

    private void ResetWaveform()
    {
        waveformParser.Mode = waveformMode.Text == "二进制帧" ? WaveformMode.BinaryFrame : WaveformMode.CsvText;
        waveformParser.SampleType = Enum.Parse<WaveformSampleType>(waveformType.Text);
        waveformParser.FrameLength = waveformLength.Value;
        waveformParser.Reset();
        wavePoints.Clear();
        sampleIndex = 0;
        waveformStream = null;
        graph.Reset();
        waveformStatus.Text = "0 个采样";
    }

    internal void FeedWaveform(SerialTrafficEvent item)
    {
        if (item.Direction != SerialDirection.Receive || item.Data.Length == 0 || waveformPaused.Value == CheckState.Checked)
        {
            return;
        }
        var stream = (item.ConnectionId, item.SegmentId);
        if (waveformStream != stream)
        {
            ResetWaveform();
            waveformStream = stream;
        }
        var samples = waveformParser.Feed(item.Data);
        foreach (var sample in samples)
        {
            while (wavePoints.Count < sample.Length)
            {
                var points = new List<PointF>();
                wavePoints.Add(points);
                graph.Series.Add(new ScatterSeries { Points = points });
                graph.Annotations.Add(new PathAnnotation { Points = points, BeforeSeries = true });
            }

            for (var index = 0; index < sample.Length; index++)
            {
                if (float.IsFinite((float)sample[index]))
                {
                    wavePoints[index].Add(new PointF(sampleIndex, (float)sample[index]));
                }
                if (wavePoints[index].Count > 2000)
                {
                    wavePoints[index].RemoveAt(0);
                }
            }
            sampleIndex++;
        }

        var all = wavePoints.SelectMany(static item => item).ToArray();
        waveformStatus.Text = $"{sampleIndex:N0} 个采样 · {wavePoints.Count} 通道";
        if (all.Length > 0)
        {
            var minimum = all.Min(static item => item.Y);
            var maximum = all.Max(static item => item.Y);
            if (waveformFollow.Value == CheckState.Checked)
            {
                graph.ScrollOffset = new PointF(Math.Max(0, sampleIndex - 2000), minimum);
                graph.CellSize = new PointF(Math.Max(1, Math.Min(sampleIndex, 2000) / (float)Math.Max(1, graph.Viewport.Width - 8)),
                    Math.Max(0.001f, (maximum - minimum) / Math.Max(1, graph.Viewport.Height - 4)));
            }
            graph.SetNeedsDraw();
        }
    }
}
