using System.Net.Http.Json;
using System.Text.Json;

namespace Statesman;

public sealed class StatesmanRemoteClient
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json;

    public StatesmanRemoteClient(HttpClient client, JsonSerializerOptions? json = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _json = json ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public async ValueTask<IReadOnlyList<StatesmanManifest>> GetRootsAsync(
        CancellationToken cancellationToken = default) =>
        await _client.GetFromJsonAsync<IReadOnlyList<StatesmanManifest>>("_statesman/", _json, cancellationToken)
            .ConfigureAwait(false) ?? Array.Empty<StatesmanManifest>();

    public async ValueTask<StatesmanManifest> GetManifestAsync(
        string root,
        CancellationToken cancellationToken = default) =>
        await _client.GetFromJsonAsync<StatesmanManifest>($"_statesman/{Uri.EscapeDataString(root)}/manifest", _json, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidDataException($"Remote Statesman root '{root}' returned no manifest.");

    public async ValueTask SignalAsync(
        string root,
        StateSignal signal,
        CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"_statesman/{Uri.EscapeDataString(root)}/signals/{Uri.EscapeDataString(signal.Name)}",
            new { partition = signal.Partition?.Value, metadata = signal.Metadata },
            _json,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
