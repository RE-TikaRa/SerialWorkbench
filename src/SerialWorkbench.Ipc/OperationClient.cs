using SerialWorkbench.Domain;

namespace SerialWorkbench.Ipc;

public static class OperationClient
{
    public static async Task<T> RunOperationAsync<T>(this IHostRpc client, OperationRequest request, CancellationToken cancellationToken, Action<OperationSnapshot>? progress = null, IProgress<OperationProgress>? updates = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = await client.StartOperationAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (started.Operation is not { } operation)
        {
            throw new HostOperationException(started.Error ?? new WorkbenchError("OPERATION_FAILED", "The Host did not accept the operation."));
        }

        using var tracking = (client as HostRpcClient)?.TrackForegroundOperation(operation.Id);
        try
        {
            progress?.Invoke(operation);
            var revision = operation.Revision;
            while (true)
            {
                var batch = await client.ReadOperationProgressAsync(new OperationProgressQuery(operation.Id, revision, WaitMilliseconds: 30_000), cancellationToken).ConfigureAwait(false);
                operation = batch.Operation;
                foreach (var item in batch.Updates)
                {
                    updates?.Report(item.Progress);
                }

                revision = batch.NextRevision;
                progress?.Invoke(operation);
                if (operation.State != OperationState.Running && revision >= operation.Revision)
                {
                    break;
                }
            }

            if (operation.State == OperationState.Cancelled)
            {
                throw new OperationCanceledException(operation.Error?.Message, cancellationToken);
            }

            return operation.ResultJson is { } json
                ? OperationJson.Read<T>(json)
                : throw new HostOperationException(operation.Error ?? new WorkbenchError("OPERATION_FAILED", "The operation has no result.", operation.Request.ConnectionId, operation.Id));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await client.CancelOperationAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
