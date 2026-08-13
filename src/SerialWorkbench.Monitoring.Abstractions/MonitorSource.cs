using SerialWorkbench.Domain;

namespace SerialWorkbench.Monitoring.Abstractions;

public interface IMonitorSource : IAsyncDisposable
{
    string Id { get; }

    IAsyncEnumerable<MonitorEvent> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed record MonitorEvent(DateTimeOffset Utc, string ProcessName, SerialDirection Direction, byte[] Data, string? ControlEvent = null);
