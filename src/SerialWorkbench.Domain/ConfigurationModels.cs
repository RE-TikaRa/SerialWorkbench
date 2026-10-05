namespace SerialWorkbench.Domain;

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
    string? DeviceInstanceId = null,
    bool Rs485Mode = false,
    int RtsBeforeSendMilliseconds = 0,
    int RtsAfterSendMilliseconds = 0,
    bool AutoReconnect = true);

public sealed record ConfigurationSnapshot(long Revision, IReadOnlyList<SerialProfile> Profiles, IReadOnlyList<string> SendHistory);

public sealed record SaveSerialProfileRequest(SerialProfile Profile, string? OriginalName = null);
