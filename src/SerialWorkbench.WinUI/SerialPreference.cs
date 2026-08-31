using System.Text.Json;
using SerialWorkbench.Domain;

namespace SerialWorkbench.WinUI;

public sealed record SerialPreference(
    string? PortName,
    int BaudRate,
    int DataBits,
    SerialParity Parity,
    SerialStopBits StopBits,
    SerialHandshake Handshake,
    string EncodingName,
    bool DtrEnable,
    bool RtsEnable,
    int MonitorFormatIndex,
    int SendFormatIndex,
    int SendLineEndingIndex,
    int SendChecksumIndex,
    int LoopIntervalMilliseconds,
    int PlotModeIndex,
    int PlotFrameLength,
    int PlotSampleTypeIndex,
    SerialConnectionRole Role = SerialConnectionRole.Dut,
    string? DeviceInstanceId = null);

public sealed record SerialProfile(
    string Name,
    string? PortName,
    int BaudRate,
    int DataBits,
    SerialParity Parity,
    SerialStopBits StopBits,
    SerialHandshake Handshake,
    string EncodingName,
    bool DtrEnable,
    bool RtsEnable,
    SerialConnectionRole Role = SerialConnectionRole.Dut,
    string? DeviceInstanceId = null);

public static class SerialPreferenceStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "data", "settings", "serial-preferences.json");

    public static SerialPreference Load() => TryLoad() ?? new SerialPreference(
        null,
        115200,
        8,
        SerialParity.None,
        SerialStopBits.One,
        SerialHandshake.None,
        "utf-8",
        false,
        false,
        0,
        0,
        0,
        0,
        1000,
        0,
        8,
        0,
        SerialConnectionRole.Dut);

    public static void Save(SerialPreference preference)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(preference));
    }

    private static SerialPreference? TryLoad()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SerialPreference>(File.ReadAllText(FilePath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

public static class WorkspacePreferenceStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "data", "settings", "workspace.json");

    public static string? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath))?.Path : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string? path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Document(path)));
    }

    private sealed record Document(string? Path);
}

public static class SerialProfileStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "data", "settings", "serial-profiles.json");

    public static IReadOnlyList<SerialProfile> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<SerialProfile>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<SerialProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles));
    }
}

public static class SendHistoryStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "data", "settings", "send-history.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<string> history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(history));
    }
}
