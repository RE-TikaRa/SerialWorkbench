using SerialWorkbench.Domain;
using SerialWorkbench.Ipc;

namespace SerialWorkbench.Cli;

internal static class ConnectionCommands
{
    public static async Task<object> ExecuteAsync(IHostRpc client, CommandArguments arguments, CancellationToken cancellationToken)
    {
        switch (arguments.CommandId)
        {
            case "profiles.show":
                return await ReadProfileAsync(client, arguments.Get("--name"), cancellationToken).ConfigureAwait(false);
            case "profiles.save":
                var options = arguments.ReadSerialOptions();
                var profile = new SerialProfile(arguments.Get("--name") ?? throw new ArgumentException("--name is required."), options.PortName,
                    options.BaudRate, options.DataBits, options.Parity, options.StopBits, options.Handshake, options.EncodingName,
                    options.DtrEnable, options.RtsEnable, options.Role, options.DeviceInstanceId, options.Rs485Mode,
                    options.RtsBeforeSendMilliseconds, options.RtsAfterSendMilliseconds, options.AutoReconnect);
                return await client.SaveSerialProfileAsync(new SaveSerialProfileRequest(profile, arguments.Get("--original-name")), cancellationToken).ConfigureAwait(false);
            case "profiles.rename":
                var original = await ReadProfileAsync(client, arguments.Get("--name"), cancellationToken).ConfigureAwait(false);
                var renamed = original with { Name = arguments.Get("--new-name") ?? throw new ArgumentException("--new-name is required.") };
                return await client.SaveSerialProfileAsync(new SaveSerialProfileRequest(renamed, original.Name), cancellationToken).ConfigureAwait(false);
            case "profiles.delete":
                return await client.DeleteSerialProfileAsync(arguments.Get("--name") ?? throw new ArgumentException("--name is required."), cancellationToken).ConfigureAwait(false);
            case "connections.control-lines":
                var connectionId = Guid.Parse(arguments.Get("--id") ?? throw new ArgumentException("--id is required."));
                var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                var connection = status.Connections.FirstOrDefault(item => item.Id == connectionId)
                    ?? throw new KeyNotFoundException($"Connection {connectionId} was not found.");
                if (arguments.GetBool("--rts") is not null && (connection.Options.Rs485Mode
                    || connection.Options.Handshake is SerialHandshake.RequestToSend or SerialHandshake.RequestToSendXOnXOff))
                {
                    throw new ArgumentException("RTS 由流控或 RS-485 方向控制管理，不能手动修改。");
                }
                var lines = new SerialControlLines(arguments.GetBool("--dtr") ?? connection.ControlLines?.DtrEnable ?? connection.Options.DtrEnable,
                    arguments.GetBool("--rts") ?? connection.ControlLines?.RtsEnable ?? connection.Options.RtsEnable);
                return await client.SetControlLinesAsync(connectionId, lines, cancellationToken).ConfigureAwait(false);
            case "connections.clear-buffers":
                return await client.ClearBuffersAsync(Guid.Parse(arguments.Get("--id") ?? throw new ArgumentException("--id is required.")),
                    arguments.Has("--rx"), arguments.Has("--tx"), cancellationToken).ConfigureAwait(false);
            case "connections.break":
                return await client.SendBreakAsync(Guid.Parse(arguments.Get("--id") ?? throw new ArgumentException("--id is required.")),
                    arguments.GetInt("--duration", 100), cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentException($"Unsupported connection command: {arguments.CommandId}", nameof(arguments));
        }
    }

    public static async Task<SerialConnectionOptions> ReadOptionsAsync(IHostRpc client, CommandArguments arguments, CancellationToken cancellationToken)
    {
        if (arguments.Get("--profile") is not { } name)
        {
            return arguments.ReadSerialOptions();
        }
        var profile = await ReadProfileAsync(client, name, cancellationToken).ConfigureAwait(false);
        return new SerialConnectionOptions(profile.PortName ?? "", profile.BaudRate, profile.DataBits, profile.Parity, profile.StopBits,
            profile.Handshake, profile.DtrEnable, profile.RtsEnable, profile.EncodingName, profile.Role, profile.DeviceInstanceId,
            profile.Rs485Mode, profile.RtsBeforeSendMilliseconds, profile.RtsAfterSendMilliseconds, profile.AutoReconnect);
    }

    private static async Task<SerialProfile> ReadProfileAsync(IHostRpc client, string? name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var configuration = await client.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return configuration.Profiles.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Serial profile {name} was not found.");
    }
}
