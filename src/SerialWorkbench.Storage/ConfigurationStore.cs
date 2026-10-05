using System.Text.Json;
using SerialWorkbench.Domain;

namespace SerialWorkbench.Storage;

public sealed class ConfigurationStore : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string settingsRoot;
    private SerialProfile[] profiles;
    private string[] history;
    private long revision = 1;

    public ConfigurationStore(ApplicationPaths paths)
    {
        settingsRoot = Path.Combine(paths.DataRoot, "settings");
        profiles = Load<SerialProfile>("serial-profiles.json");
        history = Load<string>("send-history.json");
    }

    public long Revision => Interlocked.Read(ref revision);

    public async Task<ConfigurationSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Snapshot();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConfigurationSnapshot> SaveProfileAsync(SaveSerialProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Profile.Name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var original = request.OriginalName is null ? -1 : Array.FindIndex(profiles, item => item.Name.Equals(request.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (request.OriginalName is not null && original < 0)
            {
                throw new KeyNotFoundException($"Serial profile {request.OriginalName} was not found.");
            }
            if (profiles.Where((_, index) => index != original).Any(item => item.Name.Equals(request.Profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Serial profile {request.Profile.Name} already exists.");
            }
            var next = profiles.ToList();
            if (original < 0)
            {
                next.Add(request.Profile);
            }
            else
            {
                next[original] = request.Profile;
            }
            await SaveAsync("serial-profiles.json", next, cancellationToken).ConfigureAwait(false);
            profiles = next.ToArray();
            Interlocked.Increment(ref revision);
            return Snapshot();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConfigurationSnapshot> DeleteProfileAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = profiles.Where(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            await SaveAsync("serial-profiles.json", next, cancellationToken).ConfigureAwait(false);
            profiles = next;
            Interlocked.Increment(ref revision);
            return Snapshot();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ConfigurationSnapshot> AddHistoryAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (text.Length > 0)
            {
                var next = history.Where(item => item != text).Prepend(text).Take(20).ToArray();
                await SaveAsync("send-history.json", next, cancellationToken).ConfigureAwait(false);
                history = next;
                Interlocked.Increment(ref revision);
            }
            return Snapshot();
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private ConfigurationSnapshot Snapshot() => new(Revision, profiles.ToArray(), history.ToArray());

    private T[] Load<T>(string name)
    {
        try
        {
            var path = Path.Combine(settingsRoot, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T[]>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task SaveAsync<T>(string name, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(settingsRoot);
        await File.WriteAllTextAsync(Path.Combine(settingsRoot, name), JsonSerializer.Serialize(value), cancellationToken).ConfigureAwait(false);
    }
}
