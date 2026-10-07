using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SerialWorkbench.Ipc;

internal static unsafe partial class HostProcessLauncher
{
    private const uint DetachedProcess = 0x00000008;
    private const uint StillActive = 259;

    public static StartedProcess Start(string hostPath, string applicationRoot)
    {
        var startupInfo = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
        };
        var commandLine = ($"\"{hostPath}\" --app-root \"{applicationRoot}\"" + '\0').ToCharArray();
        ProcessInformation processInformation;

        fixed (char* applicationName = hostPath)
        fixed (char* mutableCommandLine = commandLine)
        fixed (char* currentDirectory = applicationRoot)
        {
            if (CreateProcess(
                applicationName,
                mutableCommandLine,
                0,
                0,
                0,
                DetachedProcess,
                0,
                currentDirectory,
                ref startupInfo,
                out processInformation) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to start SW_HOST.exe.");
            }
        }

        try
        {
            return new StartedProcess(processInformation.Process);
        }
        finally
        {
            CloseHandle(processInformation.Thread);
        }
    }

    internal sealed class StartedProcess(nint handle) : IDisposable
    {
        public bool HasExited => GetExitCode() != StillActive;

        public uint ExitCode => GetExitCode();

        public void Dispose() => CloseHandle(handle);

        private uint GetExitCode()
        {
            if (GetExitCodeProcess(handle, out var exitCode) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return exitCode;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
    private static partial int CreateProcess(
        char* applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        int inheritHandles,
        uint creationFlags,
        nint environment,
        char* currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetExitCodeProcess(nint process, out uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Count;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ProcessInformation
    {
        public readonly nint Process;
        public readonly nint Thread;
        public readonly uint ProcessId;
        public readonly uint ThreadId;
    }
}
