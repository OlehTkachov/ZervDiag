using System.Text.Json;
using System.Text.Json.Serialization;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Profiles;

namespace CraneCAN.Core.Storage;

public static class GuidedJsonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Task SaveProfileAsync(
        string path,
        MachineProfile profile,
        CancellationToken cancellationToken = default) =>
        SaveAsync(path, profile, cancellationToken);

    public static Task<MachineProfile> LoadProfileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        LoadAsync<MachineProfile>(path, cancellationToken);

    public static Task SaveExperimentAsync(
        string path,
        GuidedExperiment experiment,
        CancellationToken cancellationToken = default) =>
        SaveAsync(path, experiment, cancellationToken);

    public static Task<GuidedExperiment> LoadExperimentAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        LoadAsync<GuidedExperiment>(path, cancellationToken);

    private static async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
        return value ?? throw new InvalidDataException($"Файл {Path.GetFileName(path)} не содержит корректных данных.");
    }
}
