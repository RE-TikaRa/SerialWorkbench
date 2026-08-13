using SerialWorkbench.Domain;

namespace SerialWorkbench.Extensions.Abstractions;

public interface IProtocolExtension
{
    string Id { get; }

    Version Version { get; }

    ValueTask<IReadOnlyList<ProtocolExtensionFrame>> ParseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask<byte[]> GenerateAsync(string operation, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken);
}

public sealed record ProtocolExtensionFrame(int Offset, int Length, IReadOnlyDictionary<string, object?> Fields, IReadOnlyList<DataChannelPoint> Channels);
