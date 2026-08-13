using System.Management;
using System.Text.RegularExpressions;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Serial.Windows;

public static partial class SerialPortCatalog
{
    public static Task<IReadOnlyList<SerialPortDescriptor>> GetPortsAsync(CancellationToken cancellationToken) =>
        Task.Run(GetPorts, cancellationToken);

    private static IReadOnlyList<SerialPortDescriptor> GetPorts()
    {
        var availablePorts = System.IO.Ports.SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var descriptors = new Dictionary<string, SerialPortDescriptor>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new ManagementObjectSearcher("SELECT Name, DeviceID, PNPDeviceID, Status FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
        using var devices = searcher.Get();
        foreach (ManagementBaseObject device in devices)
        {
            using (device)
            {
                var name = device["Name"] as string;
                if (name is null)
                {
                    continue;
                }

                var portMatch = PortNamePattern().Match(name);
                if (!portMatch.Success || !availablePorts.Contains(portMatch.Groups[1].Value))
                {
                    continue;
                }

                var portName = portMatch.Groups[1].Value;
                var instanceId = device["PNPDeviceID"] as string ?? device["DeviceID"] as string;
                var identity = instanceId ?? "";
                var vidPidMatch = VidPidPattern().Match(identity);
                var description = PortNamePattern().Replace(name, "").Trim();
                var displayName = description.Length == 0 ? portName : $"{portName} - {description}";
                descriptors[portName] = new SerialPortDescriptor(
                    portName,
                    displayName,
                    instanceId,
                    vidPidMatch.Success ? vidPidMatch.Groups[1].Value : null,
                    vidPidMatch.Success ? vidPidMatch.Groups[2].Value : null,
                    string.Equals(device["Status"] as string, "OK", StringComparison.OrdinalIgnoreCase));
            }
        }

        foreach (var portName in availablePorts)
        {
            descriptors.TryAdd(portName, new SerialPortDescriptor(portName, portName));
        }

        return descriptors.Values.OrderBy(static item => PortSortKey(item.PortName)).ToArray();
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4}).*PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VidPidPattern();

    [GeneratedRegex(@"\((COM\d+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PortNamePattern();

    private static (string Prefix, int Number) PortSortKey(string portName)
    {
        var index = portName.Length;
        while (index > 0 && char.IsDigit(portName[index - 1]))
        {
            index--;
        }

        return (portName[..index], int.TryParse(portName[index..], out var number) ? number : int.MaxValue);
    }
}
