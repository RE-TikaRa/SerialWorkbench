using System.Text.Json;
using SerialWorkbench.Application;
using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;
using SerialWorkbench.Modbus;
using SerialWorkbench.Storage;

namespace SerialWorkbench.Host;

public sealed class OperationManager(HostRuntime runtime) : IAsyncDisposable
{
    private readonly OperationStore store = new(runtime.Paths);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private readonly Dictionary<Guid, OperationSnapshot> operations = [];
    private readonly Dictionary<Guid, CancellationTokenSource> cancellations = [];
    private readonly Dictionary<Guid, Task> tasks = [];
    private readonly Dictionary<Guid, List<OperationProgressEvent>> updates = [];
    private TaskCompletionSource changed = NewSignal();
    private bool initialized;

    public int ActiveCount
    {
        get
        {
            lock (stateGate)
            {
                return cancellations.Count;
            }
        }
    }

    public async Task<StartOperationResult> StartAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            request = Normalize(request);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidDataException or FormatException)
        {
            return new StartOperationResult(null, new WorkbenchError("INVALID_ARGUMENT", ex.Message, request.ConnectionId));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (request.RequestId is { } requestId)
            {
                OperationSnapshot? existing;
                lock (stateGate)
                {
                    existing = operations.Values.FirstOrDefault(item => item.Request.RequestId == requestId);
                }

                if (existing is not null)
                {
                    return existing.Request == request
                        ? new StartOperationResult(existing)
                        : new StartOperationResult(null, new WorkbenchError("REQUEST_ID_CONFLICT", "The requestId was already used with different arguments.", request.ConnectionId, existing.Id));
                }
            }

            if (!runtime.Connections.GetSnapshots().Any(item => item.Id == request.ConnectionId && item.State == ConnectionState.Open))
            {
                return new StartOperationResult(null, new WorkbenchError("CONNECTION_NOT_FOUND", "The connection is not open.", request.ConnectionId));
            }

            var id = Guid.NewGuid();
            if (!runtime.Leases.TryAcquire(request.ConnectionId, id.ToString("D"), out var lease, out var owner))
            {
                return new StartOperationResult(null, new WorkbenchError("CONNECTION_BUSY", "The connection is occupied by another operation.", request.ConnectionId, Guid.TryParse(owner, out var occupyingId) ? occupyingId : null, owner));
            }

            var now = DateTimeOffset.UtcNow;
            var snapshot = new OperationSnapshot(id, request, OperationState.Running, ExecutionOutcome.Pending, now, now);
            try
            {
                await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(runtime.Stopping);
            lock (stateGate)
            {
                operations.Add(id, snapshot);
                cancellations.Add(id, cancellation);
                updates.Add(id, []);
                tasks.Add(id, Task.Run(() => ExecuteAsync(snapshot, lease, cancellation), CancellationToken.None));
                SignalChanged();
            }

            return new StartOperationResult(snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<OperationSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            return operations.Values.OrderByDescending(static item => item.CreatedUtc).ToArray();
        }
    }

    public async Task<OperationSnapshot> ReadAsync(OperationQuery query, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(query.WaitMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.WaitMilliseconds, 30_000);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        Task notification;
        lock (stateGate)
        {
            var snapshot = Get(query.Id);
            if (snapshot.State != OperationState.Running || snapshot.Revision > query.AfterRevision || query.WaitMilliseconds == 0)
            {
                return snapshot;
            }

            notification = changed.Task;
        }

        try
        {
            await notification.WaitAsync(TimeSpan.FromMilliseconds(query.WaitMilliseconds), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        lock (stateGate)
        {
            return Get(query.Id);
        }
    }

    public async Task<OperationSnapshot> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        Task? task;
        lock (stateGate)
        {
            _ = Get(id);
            if (cancellations.TryGetValue(id, out var cancellation))
            {
                cancellation.Cancel();
            }

            task = tasks.GetValueOrDefault(id);
        }

        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (stateGate)
        {
            return Get(id);
        }
    }

    public async Task<OperationProgressBatch> ReadProgressAsync(OperationProgressQuery query, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.MaximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.MaximumCount, 10_000);
        await ReadAsync(new OperationQuery(query.Id, query.AfterRevision, query.WaitMilliseconds), cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            var snapshot = Get(query.Id);
            var entries = updates.GetValueOrDefault(query.Id)?.Where(item => item.Revision > query.AfterRevision).Take(query.MaximumCount).ToArray() ?? [];
            return new OperationProgressBatch(snapshot, entries, entries.Length == query.MaximumCount ? entries[^1].Revision : snapshot.Revision);
        }
    }

    public async Task CancelConnectionAsync(Guid connectionId, CancellationToken cancellationToken, bool preserveReads = false)
    {
        var running = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.Request.ConnectionId == connectionId && item.State == OperationState.Running
                && (!preserveReads || item.Request.Command != "modbus.poll"));
        foreach (var operation in running)
        {
            await CancelAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<T> RunAsync<T>(OperationRequest request, CancellationToken cancellationToken)
    {
        var started = await StartAsync(request, cancellationToken).ConfigureAwait(false);
        if (started.Operation is not { } operation)
        {
            throw new InvalidOperationException($"{started.Error?.Code}: {started.Error?.Message}");
        }

        do
        {
            operation = await ReadAsync(new OperationQuery(operation.Id, operation.Revision, 30_000), cancellationToken).ConfigureAwait(false);
        }
        while (operation.State == OperationState.Running);

        return operation.ResultJson is { } json
            ? OperationJson.Read<T>(json)
            : throw new InvalidOperationException($"{operation.Error?.Code}: {operation.Error?.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        Task[] remaining;
        lock (stateGate)
        {
            foreach (var cancellation in cancellations.Values)
            {
                cancellation.Cancel();
            }

            remaining = [.. tasks.Values];
        }

        await Task.WhenAll(remaining).ConfigureAwait(false);
        await store.DisposeAsync().ConfigureAwait(false);
        gate.Dispose();
    }

    private async Task ExecuteAsync(OperationSnapshot snapshot, WriteLeaseManager.WriteLease lease, CancellationTokenSource cancellation)
    {
        object? result = null;
        WorkbenchError? error = null;
        var state = OperationState.Succeeded;
        var outcome = ExecutionOutcome.Confirmed;
        try
        {
            var request = snapshot.Request;
            var token = cancellation.Token;
            void Report(OperationProgress progress) => UpdateProgress(snapshot.Id, progress);
            switch (request.Command)
            {
                case "send":
                    var send = OperationJson.Read<SendRequest>(request.ParametersJson);
                    await runtime.Connections.SendAsync(request.ConnectionId, send.Data, send.Source, token, lease).ConfigureAwait(false);
                    result = new { success = true, bytes = send.Data.Length, hex = Convert.ToHexString(send.Data) };
                    Report(new OperationProgress(send.Data.Length, send.Data.Length, "bytes"));
                    break;
                case "loopback.run":
                    var loopback = OperationJson.Read<LoopbackRequest>(request.ParametersJson);
                    var loopbackResult = await runtime.Connections.RunLoopbackAsync(loopback, token, lease, Report).ConfigureAwait(false);
                    await runtime.Sessions.AppendLoopbackResultAsync(loopback, loopbackResult, token).ConfigureAwait(false);
                    result = loopbackResult;
                    error = loopbackResult.Passed ? null : Failure(loopbackResult.ErrorCode, loopbackResult.Error, snapshot);
                    break;
                case "sequence.run":
                    result = await runtime.Connections.RunSequenceAsync(request.ConnectionId, OperationJson.Read<SerialSequenceDefinition>(request.ParametersJson), token, lease, Report).ConfigureAwait(false);
                    break;
                case "xmodem.send":
                    var transfer = await runtime.Connections.SendXmodemAsync(request.ConnectionId, OperationJson.Read<byte[]>(request.ParametersJson), token, lease, Report).ConfigureAwait(false);
                    result = transfer;
                    error = transfer.Success ? null : Failure(transfer.ErrorCode, transfer.Error, snapshot);
                    break;
                case "xmodem.receive":
                    var received = await runtime.Connections.ReceiveXmodemAsync(request.ConnectionId, token, lease, Report).ConfigureAwait(false);
                    result = received;
                    error = received.Result.Success ? null : Failure(received.Result.ErrorCode, received.Result.Error, snapshot);
                    break;
                case "modbus.read":
                case "modbus.write":
                    var modbus = await runtime.Connections.RunModbusAsync(OperationJson.Read<ModbusTransactionRequest>(request.ParametersJson), token, lease).ConfigureAwait(false);
                    result = modbus;
                    error = modbus.Success ? null : Failure(modbus.ErrorCode, modbus.Error, snapshot);
                    break;
                case "modbus.scan":
                    result = await ScanAsync(OperationJson.Read<ModbusScanRequest>(request.ParametersJson), lease, Report, token).ConfigureAwait(false);
                    break;
                case "modbus.poll":
                    var poll = await PollAsync(OperationJson.Read<ModbusPollRequest>(request.ParametersJson), lease, Report, token).ConfigureAwait(false);
                    result = poll;
                    error = poll.Failed == 0 ? null : Failure(poll.Samples.First(static item => !item.Result.Success).Result.ErrorCode, "One or more Modbus samples failed.", snapshot);
                    break;
            }

            if (error is not null)
            {
                state = OperationState.Failed;
                outcome = error.Code is "MODBUS_EXCEPTION" or "DATA_MISMATCH" ? ExecutionOutcome.Confirmed : ExecutionOutcome.Unknown;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            state = OperationState.Cancelled;
            outcome = ExecutionOutcome.Unknown;
            error = Failure("CANCELLED", "Operation cancelled.", snapshot);
        }
        catch (Exception ex)
        {
            state = OperationState.Failed;
            outcome = ExecutionOutcome.Unknown;
            error = Failure(ex switch
            {
                TimeoutException => "TIMEOUT",
                System.Threading.Channels.ChannelClosedException => "DEVICE_DISCONNECTED",
                ArgumentException => "INVALID_ARGUMENT",
                IOException or UnauthorizedAccessException => "IO_ERROR",
                _ => "OPERATION_FAILED",
            }, ex.Message, snapshot);
        }
        finally
        {
            OperationSnapshot completed;
            lock (stateGate)
            {
                var current = Get(snapshot.Id);
                completed = current with
                {
                    State = state,
                    Outcome = outcome,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Revision = current.Revision + 1,
                    ResultJson = result is null ? null : JsonSerializer.Serialize(result, OperationJson.Options),
                    Error = error,
                };
            }

            try
            {
                await store.SaveAsync(completed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                completed = completed with { State = OperationState.Failed, Outcome = ExecutionOutcome.Unknown, Error = Failure("RESULT_PERSISTENCE_FAILED", ex.Message, snapshot) };
            }
            finally
            {
                lease.Dispose();
                cancellation.Dispose();
                lock (stateGate)
                {
                    operations[snapshot.Id] = completed;
                    cancellations.Remove(snapshot.Id);
                    tasks.Remove(snapshot.Id);
                    SignalChanged();
                }
            }
        }
    }

    private async Task<ModbusBatchResult> ScanAsync(ModbusScanRequest request, WriteLeaseManager.WriteLease lease, Action<OperationProgress> report, CancellationToken cancellationToken)
    {
        var samples = new List<ModbusSample>();
        for (var address = (int)request.FirstAddress; address <= request.LastAddress; address++)
        {
            var slave = (byte)address;
            var frame = ModbusRtuCodec.BuildReadRequest(slave, 3, request.RegisterAddress, 1);
            var response = await runtime.Connections.RunModbusAsync(new ModbusTransactionRequest(request.ConnectionId, frame, slave, 3, request.TimeoutMilliseconds), cancellationToken, lease).ConfigureAwait(false);
            var sample = new ModbusSample(samples.Count + 1, slave, DateTimeOffset.UtcNow, response);
            samples.Add(sample);
            report(new OperationProgress(samples.Count, request.LastAddress - request.FirstAddress + 1, "addresses", ItemJson: JsonSerializer.Serialize(sample, OperationJson.Options)));
            if (address < request.LastAddress && request.IntervalMilliseconds > 0)
            {
                await Task.Delay(request.IntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ModbusBatchResult(samples, samples.Count(static item => item.Result.Success || item.Result.ExceptionCode is not null), samples.Count(static item => !item.Result.Success && item.Result.ExceptionCode is null));
    }

    private async Task<ModbusBatchResult> PollAsync(ModbusPollRequest request, WriteLeaseManager.WriteLease lease, Action<OperationProgress> report, CancellationToken cancellationToken)
    {
        var samples = new List<ModbusSample>();
        for (var index = 0; index < request.Count; index++)
        {
            await runtime.Connections.WaitForOpenAsync(request.ConnectionId, cancellationToken).ConfigureAwait(false);
            var segmentId = runtime.Connections.GetSnapshots().First(item => item.Id == request.ConnectionId).SegmentId;
            var frame = ModbusRtuCodec.BuildReadRequest(request.SlaveAddress, request.FunctionCode, request.Address, request.Quantity);
            ModbusTransactionResult response;
            try
            {
                response = await runtime.Connections.RunModbusAsync(new ModbusTransactionRequest(request.ConnectionId, frame, request.SlaveAddress, request.FunctionCode, request.TimeoutMilliseconds), cancellationToken, lease).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is (IOException or InvalidOperationException or System.Threading.Channels.ChannelClosedException)
                && runtime.Connections.GetSnapshots().Any(item => item.Id == request.ConnectionId && (item.State != ConnectionState.Open || item.SegmentId != segmentId)))
            {
                response = new ModbusTransactionResult(false, [], request.FunctionCode, [], null, null, null, TimeSpan.Zero, "Device disconnected.", ErrorCode: "DEVICE_DISCONNECTED");
            }
            var sample = new ModbusSample(index + 1, request.SlaveAddress, DateTimeOffset.UtcNow, response);
            samples.Add(sample);
            report(new OperationProgress(index + 1, request.Count, "samples", ItemJson: JsonSerializer.Serialize(sample, OperationJson.Options)));
            if (index + 1 < request.Count && request.IntervalMilliseconds > 0)
            {
                await Task.Delay(request.IntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ModbusBatchResult(samples, samples.Count(static item => item.Result.Success), samples.Count(static item => !item.Result.Success));
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        foreach (var saved in await store.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = saved;
            if (snapshot.State == OperationState.Running)
            {
                snapshot = snapshot with
                {
                    State = OperationState.Interrupted,
                    Outcome = ExecutionOutcome.Unknown,
                    Error = Failure("HOST_RESTARTED", "The Host restarted before the operation completed. Device actions will not be replayed.", snapshot),
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    Revision = snapshot.Revision + 1,
                };
                await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            }

            lock (stateGate)
            {
                operations.Add(snapshot.Id, snapshot);
            }
        }

        initialized = true;
    }

    private void UpdateProgress(Guid id, OperationProgress progress)
    {
        lock (stateGate)
        {
            var current = Get(id);
            var now = DateTimeOffset.UtcNow;
            operations[id] = current with { Progress = progress, UpdatedUtc = now, Revision = current.Revision + 1 };
            updates[id].Add(new OperationProgressEvent(current.Revision + 1, now, progress));
            SignalChanged();
        }
    }

    private OperationSnapshot Get(Guid id) => operations.GetValueOrDefault(id) ?? throw new KeyNotFoundException($"Operation {id} was not found.");

    private void SignalChanged()
    {
        var previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WorkbenchError Failure(string? code, string? message, OperationSnapshot snapshot) =>
        new(code ?? "PROTOCOL_ERROR", message ?? "Operation failed.", snapshot.Request.ConnectionId, snapshot.Id);

    private static OperationRequest Normalize(OperationRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Command);
        if (request.RequestId is { } requestId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        }

        object parameters = request.Command switch
        {
            "send" => OperationJson.Read<SendRequest>(request.ParametersJson) with { ConnectionId = request.ConnectionId },
            "loopback.run" => OperationJson.Read<LoopbackRequest>(request.ParametersJson) with { ConnectionId = request.ConnectionId },
            "sequence.run" => OperationJson.Read<SerialSequenceDefinition>(request.ParametersJson),
            "xmodem.send" => OperationJson.Read<byte[]>(request.ParametersJson),
            "xmodem.receive" => new { },
            "modbus.read" or "modbus.write" => OperationJson.Read<ModbusTransactionRequest>(request.ParametersJson) with { ConnectionId = request.ConnectionId },
            "modbus.scan" => OperationJson.Read<ModbusScanRequest>(request.ParametersJson) with { ConnectionId = request.ConnectionId },
            "modbus.poll" => OperationJson.Read<ModbusPollRequest>(request.ParametersJson) with { ConnectionId = request.ConnectionId },
            _ => throw new ArgumentException($"Unsupported operation: {request.Command}", nameof(request)),
        };

        switch (parameters)
        {
            case SendRequest send:
                ArgumentNullException.ThrowIfNull(send.Data);
                ArgumentException.ThrowIfNullOrWhiteSpace(send.Source);
                break;
            case LoopbackRequest loopback:
                CheckRange(loopback.PayloadLength, 1, 16 * 1024 * 1024, nameof(loopback.PayloadLength));
                CheckRange(loopback.Iterations, 1, 10_000, nameof(loopback.Iterations));
                CheckRange(loopback.TimeoutMilliseconds, 1, 600_000, nameof(loopback.TimeoutMilliseconds));
                break;
            case SerialSequenceDefinition sequence:
                ArgumentException.ThrowIfNullOrWhiteSpace(sequence.Name);
                ArgumentNullException.ThrowIfNull(sequence.Steps);
                CheckRange(sequence.Steps.Count, 1, 10_000, nameof(sequence.Steps));
                foreach (var step in sequence.Steps)
                {
                    ArgumentNullException.ThrowIfNull(step.Data);
                    CheckRange(step.RepeatCount, 1, 10_000, nameof(step.RepeatCount));
                    CheckRange(step.DelayMilliseconds, 0, 600_000, nameof(step.DelayMilliseconds));
                    CheckRange(step.WaitMilliseconds, 0, 600_000, nameof(step.WaitMilliseconds));
                    CheckRange(step.ResponseTimeoutMilliseconds, 0, 600_000, nameof(step.ResponseTimeoutMilliseconds));
                    CheckRange(step.RetryCount, 0, 100, nameof(step.RetryCount));
                    if (step.ResponseHex is { } hex)
                    {
                        _ = SerialWorkbench.Protocols.HexCodec.Parse(hex);
                    }
                }
                break;
            case ModbusTransactionRequest modbus:
                CheckRange(modbus.TimeoutMilliseconds, 1, 600_000, nameof(modbus.TimeoutMilliseconds));
                ArgumentNullException.ThrowIfNull(modbus.Frame);
                var writeFunction = modbus.FunctionCode is 5 or 6 or 15 or 16;
                if (writeFunction != (request.Command == "modbus.write")
                    || modbus.FunctionCode is not (1 or 2 or 3 or 4 or 5 or 6 or 15 or 16 or 17)
                    || (modbus.FunctionCode == 17 ? modbus.Frame.Length != 4 : modbus.Frame.Length < 8)
                    || !ModbusRtuCodec.HasValidCrc(modbus.Frame) || modbus.Frame[0] != modbus.SlaveAddress || modbus.Frame[1] != modbus.FunctionCode)
                {
                    throw new InvalidDataException("Modbus request metadata, frame length or CRC is invalid.");
                }
                break;
            case ModbusScanRequest scan:
                CheckRange(scan.FirstAddress, 1, 247, nameof(scan.FirstAddress));
                CheckRange(scan.LastAddress, scan.FirstAddress, 247, nameof(scan.LastAddress));
                CheckRange(scan.TimeoutMilliseconds, 1, 600_000, nameof(scan.TimeoutMilliseconds));
                CheckRange(scan.IntervalMilliseconds, 0, 600_000, nameof(scan.IntervalMilliseconds));
                break;
            case ModbusPollRequest poll:
                CheckRange(poll.SlaveAddress, 1, 247, nameof(poll.SlaveAddress));
                CheckRange(poll.FunctionCode, 1, 4, nameof(poll.FunctionCode));
                CheckRange(poll.Quantity, 1, poll.FunctionCode is 1 or 2 ? 2000 : 125, nameof(poll.Quantity));
                CheckRange(poll.Count, 1, 100_000, nameof(poll.Count));
                CheckRange(poll.TimeoutMilliseconds, 1, 600_000, nameof(poll.TimeoutMilliseconds));
                CheckRange(poll.IntervalMilliseconds, 0, 600_000, nameof(poll.IntervalMilliseconds));
                break;
        }

        return request with { ParametersJson = JsonSerializer.Serialize(parameters, OperationJson.Options) };
    }

    private static void CheckRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
        }
    }
}
