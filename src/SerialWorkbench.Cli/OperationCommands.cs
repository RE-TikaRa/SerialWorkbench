using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;

namespace SerialWorkbench.Cli;

internal static class OperationCommands
{
    public static async Task<OperationSnapshot> WaitAsync(IHostRpc client, Guid id, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMilliseconds);
        try
        {
            var operation = await client.ReadOperationAsync(new OperationQuery(id), timeout.Token).ConfigureAwait(false);
            while (operation.State == OperationState.Running)
            {
                operation = await client.ReadOperationAsync(new OperationQuery(id, operation.Revision, 30_000), timeout.Token).ConfigureAwait(false);
            }
            return operation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"等待任务 {id} 超时，任务继续在 Host 中执行。");
        }
    }

    public static int ExitCode(OperationSnapshot operation) => operation.State switch
    {
        OperationState.Succeeded or OperationState.Running => 0,
        OperationState.Cancelled => 5,
        _ => operation.Error?.Code == "TIMEOUT" ? 4 : 3,
    };
}
