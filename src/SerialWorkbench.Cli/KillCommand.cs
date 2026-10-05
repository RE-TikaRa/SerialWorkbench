using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SerialWorkbench.Ipc;
using StreamJsonRpc;

namespace SerialWorkbench.Cli;

internal static partial class KillCommand
{
    private static readonly string[] processNames = ["SW", "SW_TUI", "SW_CLI", "SW_HOST"];
    private const uint QueryLimitedInformation = 0x1000;

    public static async Task<KillReceipt> ExecuteAsync(string applicationRoot, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot));
        var results = new List<ProcessStopResult>();
        var processes = new List<Process>();
        var closedConnections = new List<Guid>();
        var hostStoppedGracefully = false;
        string? shutdownError = null;
        try
        {
            foreach (var name in processNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        process.Dispose();
                        continue;
                    }
                    try
                    {
                        if (!process.HasExited && IsProjectProcess(root, ReadExecutablePath(process)))
                        {
                            processes.Add(process);
                        }
                        else
                        {
                            process.Dispose();
                        }
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        results.Add(new ProcessStopResult(process.Id, name, false, false, ex.Message));
                        process.Dispose();
                    }
                }
            }

            foreach (var process in processes.Where(static item => !item.ProcessName.Equals("SW_HOST", StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(await StopProcessAsync(process, timeoutMilliseconds, cancellationToken).ConfigureAwait(false));
            }

            var hosts = processes.Where(static item => item.ProcessName.Equals("SW_HOST", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (hosts.Length > 0)
            {
                using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                shutdown.CancelAfter(timeoutMilliseconds);
                try
                {
                    await using (var client = await HostEndpoint.ConnectAsync(root, false, shutdown.Token).ConfigureAwait(false))
                    {
                        var status = await client.GetStatusAsync(shutdown.Token).ConfigureAwait(false);
                        foreach (var connection in status.Connections)
                        {
                            var closed = await client.CloseConnectionAsync(connection.Id, shutdown.Token).ConfigureAwait(false);
                            if (!closed.Success)
                            {
                                throw new InvalidOperationException(closed.Error);
                            }
                            closedConnections.Add(connection.Id);
                        }
                        var stopped = await client.StopHostAsync(shutdown.Token).ConfigureAwait(false);
                        if (!stopped.Success)
                        {
                            throw new InvalidOperationException(stopped.Error);
                        }
                    }
                    foreach (var host in hosts)
                    {
                        await host.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                    hostStoppedGracefully = true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    shutdownError = "Host 未在关闭时限内退出。";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or RemoteInvocationException or ConnectionLostException)
                {
                    shutdownError = ex.Message;
                }

                foreach (var host in hosts)
                {
                    results.Add(await StopProcessAsync(host, timeoutMilliseconds, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        var failures = results.Where(static item => !item.Stopped).ToArray();
        return new KillReceipt(failures.Length == 0, root, Environment.ProcessId, closedConnections, results, hostStoppedGracefully,
            shutdownError, failures.Length == 0 ? null : string.Join(Environment.NewLine, failures.Select(static item => $"{item.Name} ({item.ProcessId}): {item.Error}")),
            failures.Length == 0 ? null : "PROCESS_TERMINATION_FAILED");
    }

    internal static bool IsProjectProcess(string applicationRoot, string executablePath) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationRoot)), Path.GetDirectoryName(Path.GetFullPath(executablePath)), StringComparison.OrdinalIgnoreCase)
        && processNames.Contains(Path.GetFileNameWithoutExtension(executablePath), StringComparer.OrdinalIgnoreCase)
        && Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static async Task<ProcessStopResult> StopProcessAsync(Process process, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        var id = process.Id;
        var name = process.ProcessName;
        var forced = false;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                forced = true;
            }
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            return new ProcessStopResult(id, name, true, forced, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException)
        {
            return new ProcessStopResult(id, name, false, forced, ex.Message);
        }
    }

    private static unsafe string ReadExecutablePath(Process process)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, (uint)process.Id);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        Span<char> path = stackalloc char[32768];
        var length = path.Length;
        fixed (char* buffer = path)
        {
            if (!QueryFullProcessImageName(handle, 0, buffer, ref length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        return new string(path[..length]);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* executablePath, ref int length);
}
