using SerialWorkbench.Domain;

namespace SerialWorkbench.Ipc;

public sealed class HostOperationException(WorkbenchError error) : InvalidOperationException($"{error.Code}: {error.Message}")
{
    public WorkbenchError Error { get; } = error;
}
