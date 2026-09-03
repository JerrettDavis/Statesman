using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Statesman.EndToEnd.Tests;

public sealed class EndpointTests
{
    [Fact]
    public async Task Manifest_snapshot_history_and_signal_flow_end_to_end()
    {
        StateKey<ServiceState> key = StateKey.Define<ServiceState>("service/status");
        var source = new StatusSource();
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("endpoint")
            .State(key, state => state
                .Initial(new ServiceState("unknown"))
                .Refresh(refresh => refresh.OnSignal("status.changed"))
                .Load(load => load.From<IStatusSource>("status", (service, _, cancellationToken) =>
                    service.ReadAsync(cancellationToken))))
            .Build();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IStatusSource>(source);
        builder.Services.AddStatesman(declaration);
        await using WebApplication app = builder.Build();
        app.MapStatesman(options =>
        {
            options.EnableSignals = true;
            options.IncludeValuesByDefault = true;
        });
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        IStatesman runtime = app.Services.GetRequiredService<IStatesman>();
        await runtime.State(key).SetAsync(new ServiceState("seeded"));
        using JsonDocument manifest = await client.GetFromJsonAsync<JsonDocument>("/_statesman/endpoint/manifest")
            ?? throw new InvalidDataException();
        using JsonDocument snapshot = await client.GetFromJsonAsync<JsonDocument>("/_statesman/endpoint/state/service/status?includeValue=true")
            ?? throw new InvalidDataException();
        using HttpResponseMessage signal = await client.PostAsJsonAsync(
            "/_statesman/endpoint/signals/status.changed",
            new { });
        signal.EnsureSuccessStatusCode();
        using JsonDocument history = await client.GetFromJsonAsync<JsonDocument>("/_statesman/endpoint/history/service/status?take=10")
            ?? throw new InvalidDataException();

        Assert.Equal("endpoint", manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal("seeded", snapshot.RootElement.GetProperty("value").GetProperty("status").GetString());
        Assert.Equal("live", runtime.State(key).Current.RequiredValue.Status);
        Assert.True(history.RootElement.GetArrayLength() >= 2);
    }

    private interface IStatusSource
    {
        ValueTask<ServiceState> ReadAsync(CancellationToken cancellationToken);
    }

    private sealed class StatusSource : IStatusSource
    {
        public ValueTask<ServiceState> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ServiceState("live"));
        }
    }

    private sealed record ServiceState(string Status);
}
