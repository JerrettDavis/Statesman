using System.Net.Http.Json;
using System.Text.Json;

namespace Statesman;

public static class HttpStateLoadExtensions
{
    public static StateLoadBuilder<TState> FromJson<TState>(
        this StateLoadBuilder<TState> builder,
        string name,
        string clientName,
        Func<StateLoadContext, Uri> endpoint,
        JsonSerializerOptions? json = null)
    {
        return builder.From<IHttpClientFactory>(name, async (factory, context, cancellationToken) =>
        {
            HttpClient client = factory.CreateClient(clientName);
            using HttpResponseMessage response = await client.GetAsync(endpoint(context), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TState>(json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"HTTP source '{name}' returned no JSON state.");
        });
    }

    public static StateSourceBuilder<TState, TPart> FromJson<TState, TPart>(
        this StateLoadBuilder<TState> builder,
        string name,
        string clientName,
        Func<StateLoadContext, Uri> endpoint,
        JsonSerializerOptions? json = null)
    {
        return builder.From<IHttpClientFactory, TPart>(name, async (factory, context, cancellationToken) =>
        {
            HttpClient client = factory.CreateClient(clientName);
            using HttpResponseMessage response = await client.GetAsync(endpoint(context), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<TPart>(json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"HTTP source '{name}' returned no JSON facet.");
        });
    }
}
