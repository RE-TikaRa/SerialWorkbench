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
    string? DeviceInstanceId = null,
    bool Rs485Mode = false,
    int RtsBeforeSendMilliseconds = 0,
    int RtsAfterSendMilliseconds = 0,
    int HexReceiveGapMilliseconds = 10,
    bool AutoReconnect = true);

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
