using System.Text.Json;
using System.Text.Json.Serialization;
using CraneCAN.Core.Network;

namespace CraneCAN.Core.Storage;

public static class CanNetworkSnapshotCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task SaveAsync(string path, CanNetworkSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, snapshot with
        {
            ProgramVersion = "0.7.0",
            CreatedAt = DateTimeOffset.UtcNow
        }, Options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<CanNetworkSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = await JsonSerializer.DeserializeAsync<CanNetworkSnapshot>(stream, Options, cancellationToken).ConfigureAwait(false);
        return snapshot ?? throw new InvalidDataException("Файл не содержит корректного сетевого снимка CraneCAN.");
    }
}
