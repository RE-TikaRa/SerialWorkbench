namespace SerialWorkbench.Ipc;

public sealed class HostAccessException(string applicationRoot, Exception innerException)
    : UnauthorizedAccessException($"无法连接应用目录“{applicationRoot}”的 Host：现有 SW_HOST.exe 不属于当前 Windows 用户。请先结束该 Host，再重新启动客户端。", innerException)
{
    public string ApplicationRoot { get; } = applicationRoot;
}
