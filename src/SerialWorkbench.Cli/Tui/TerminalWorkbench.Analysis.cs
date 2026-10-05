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
    private readonly TableView sessions = new() { Y = 2, Width = Dim.Fill(), Height = Dim.Percent(40), FullRowSelect = true };
    private readonly TableView sessionEvents = new() { Y = Pos.Percent(40) + 3, Width = Dim.Fill(), Height = Dim.Fill(2), FullRowSelect = true };
    private readonly TextField sessionFilter = new() { Y = Pos.AnchorEnd(), Width = Dim.Fill(16) };
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
    private readonly OptionSelector<WaveformMode> waveformMode = new() { Orientation = Orientation.Horizontal };
    private readonly OptionSelector<WaveformSampleType> waveformType = new() { X = 0, Y = 1, Orientation = Orientation.Horizontal };
    private readonly NumericUpDown<int> waveformLength = new() { X = 45, Y = 0, Value = 8, Width = 12 };
    private int sampleIndex;

    private View BuildSessions()
    {
        var view = new View { Title = "会话", Width = Dim.Fill(), Height = Dim.Fill() };
        var refresh = Button("刷新", () => RunUiAsync(RefreshSessionsAsync));
        var load = Button("加载更多", () => RunUiAsync(ReadSessionAsync));
        load.X = Pos.Right(refresh) + 1;
        var export = Button("导出 CSV", () => RunUiAsync(ExportSessionAsync));
        export.X = Pos.Right(load) + 1;
        var play = Button("回放", () => RunUiAsync(ReplaySessionAsync));
        play.X = Pos.Right(export) + 1;
        var stop = Button("停止回放", () =>
        {
            replay?.Cancel();
            return Task.CompletedTask;
        });
        stop.X = Pos.Right(play) + 1;
        sessions.ValueChanged += (_, args) =>
        {
            if (args.NewValue is { } selection && selection.SelectedCell.Y >= 0 && selection.SelectedCell.Y < displayedSessions.Length)
            {
                var id = displayedSessions[selection.SelectedCell.Y].Id;
                if (selectedSession != id)
                {
                    selectedSession = id;
                    sessionRevision++;
                    sessionSequence = 0;
                    displayedSessionEvents.Clear();
                    _ = RunUiAsync(ReadSessionAsync);
                }
            }
        };
        var applyFilter = Button("筛选", () => RunUiAsync(() =>
        {
            sessionSequence = 0;
            sessionRevision++;
            displayedSessionEvents.Clear();
            return ReadSessionAsync();
        }));
        applyFilter.X = Pos.AnchorEnd();
        applyFilter.Y = Pos.AnchorEnd();
        view.Add(refresh, load, export, play, stop, sessions, sessionEvents, sessionFilter, applyFilter);
        return view;
    }

    private async Task RefreshSessionsAsync()
    {
        var listed = await client.ListSessionsAsync(lifetime.Token).ConfigureAwait(false);
        app.Invoke(() =>
        {
            displayedSessions = listed.ToArray();
            sessions.Table = new EnumerableTableSource<SessionDescriptor>(displayedSessions, new Dictionary<string, Func<SessionDescriptor, object>>
            {
                ["会话"] = item => item.Id,
                ["开始"] = item => item.StartedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                ["事件"] = item => item.EventCount,
                ["字节"] = item => item.RawByteCount,
                ["文件"] = item => item.Path,
            });
        });
    }

    private async Task ReadSessionAsync()
    {
        if (selectedSession is not { } id)
        {
            return;
        }

        var revision = sessionRevision;
        var query = new SessionEventQuery(id, 1000, sessionSequence, DataContainsHex: sessionFilter.Text.Length == 0 ? null : Convert.ToHexString(HexCodec.Parse(sessionFilter.Text)));
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

        var csv = await client.ExportSessionCsvAsync(new SessionEventQuery(id), lifetime.Token).ConfigureAwait(false);
        await File.WriteAllTextAsync(destination, csv, new UTF8Encoding(false), lifetime.Token).ConfigureAwait(false);
        app.Invoke(() => message.Text = $"已导出：{destination}");
    }

    private async Task ReplaySessionAsync()
    {
        var id = selectedSession ?? throw new InvalidOperationException("请选择会话。");
        if (replay is not null)
        {
            return;
        }

        using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
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
                while (true)
                {
                    var events = await client.ReadSessionEventsAsync(new SessionEventQuery(id, 1000, afterSequence), source.Token).ConfigureAwait(false);
                    if (events.Count == 0)
                    {
                        break;
                    }
                    foreach (var item in events)
                    {
                        source.Token.ThrowIfCancellationRequested();
                        if (previous is { } timestamp && item.Utc > timestamp)
                        {
                            await Task.Delay(item.Utc - timestamp, source.Token).ConfigureAwait(false);
                        }
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
                                    FeedWaveform(item.Data);
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
        view.Add(waveformMode, waveformType, waveformLength, graph);
        return view;
    }

    private void ResetWaveform()
    {
        waveformParser.Mode = waveformMode.Value ?? WaveformMode.CsvText;
        waveformParser.SampleType = waveformType.Value ?? WaveformSampleType.Int16LittleEndian;
        waveformParser.FrameLength = waveformLength.Value;
        waveformParser.Reset();
        wavePoints.Clear();
        sampleIndex = 0;
        graph.Reset();
    }

    private void FeedWaveform(byte[] data)
    {
        var samples = waveformParser.Feed(data);
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
                wavePoints[index].Add(new PointF(sampleIndex, (float)sample[index]));
                if (wavePoints[index].Count > 2000)
                {
                    wavePoints[index].RemoveAt(0);
                }
            }
            sampleIndex++;
        }

        var all = wavePoints.SelectMany(static item => item).ToArray();
        if (all.Length > 0)
        {
            var minimum = all.Min(static item => item.Y);
            var maximum = all.Max(static item => item.Y);
            graph.ScrollOffset = new PointF(Math.Max(0, sampleIndex - 2000), minimum);
            graph.CellSize = new PointF(Math.Max(1, Math.Min(sampleIndex, 2000) / (float)Math.Max(1, graph.Viewport.Width - 8)),
                Math.Max(0.001f, (maximum - minimum) / Math.Max(1, graph.Viewport.Height - 4)));
            graph.SetNeedsDraw();
        }
    }
}
