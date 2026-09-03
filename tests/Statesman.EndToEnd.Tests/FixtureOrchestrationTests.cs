using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Statesman.Testing;

namespace Statesman.EndToEnd.Tests;

public sealed class FixtureOrchestrationTests
{
    [Fact]
    public async Task E2e_host_can_be_seeded_before_the_first_http_request()
    {
        StateKey<UserContext> currentUser = StateKey.Define<UserContext>("session/user");
        StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("web-test")
            .State(currentUser, _ => { })
            .Build();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddStatesman(declaration);
        await using WebApplication app = builder.Build();

        app.MapGet("/me", (IStatesman statesman) =>
        {
            IStateSnapshot<UserContext> user = statesman.State(currentUser).Current;
            return user.HasValue ? Results.Ok(user.RequiredValue) : Results.NotFound();
        });

        IStatesman runtime = app.Services.GetRequiredService<IStatesman>();
        await runtime.Seed()
            .State(currentUser, new UserContext("e2e-user", new[] { "admin", "editor" }))
            .ApplyAsync();

        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        HttpResponseMessage response = await client.GetAsync("/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("e2e-user", body);
        Assert.Contains("admin", body);
    }

    private sealed record UserContext(string Id, IReadOnlyList<string> Roles);
}
