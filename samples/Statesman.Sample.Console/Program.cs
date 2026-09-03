using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Statesman;

StateKey<UserServiceState> userState = StateKey.Define<UserServiceState>("users/user");
StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("sample", "1.0")
    .Defaults(defaults => defaults
        .Freshness(freshness => freshness
            .FreshFor(TimeSpan.FromMinutes(5))
            .ServeStaleFor(TimeSpan.FromMinutes(30))
            .StaleWhileRevalidate())
        .Retain(retention => retention.Last(250).For(TimeSpan.FromDays(30))))
    .Container("users", users => users
        .State(userState, state => state
            .Partitioned()
            .Initial(new UserServiceState(string.Empty, Array.Empty<string>(), false))
            .Load(load => load
                .From<IUserService, UserProfile>("profile", (service, context, cancellationToken) =>
                    service.GetProfileAsync(context.Address.Partition.Value, cancellationToken))
                .Into((current, profile, _) => current with { DisplayName = profile.DisplayName })
                .From<IUserService, IReadOnlyList<string>>("permissions", (service, context, cancellationToken) =>
                    service.GetPermissionsAsync(context.Address.Partition.Value, cancellationToken))
                .Into((current, permissions, _) => current with { Permissions = permissions })
                .InParallel()
                .RequireAll())
            .Refresh(refresh => refresh
                .OnFirstRead()
                .WhenStale()
                .Every(TimeSpan.FromMinutes(10))
                .OnSignal("user.changed"))
            .Interact<SetEnabled>("set-enabled", interaction => interaction
                .Describe("Enables or disables the user in the local authoritative view.")
                .Reduce((current, command) => current with { Enabled = command.Enabled }))))
    .Build();

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IUserService, DemoUserService>();
builder.Services.AddStatesman(declaration);
builder.Services.AddStatesmanHosting();
using IHost host = builder.Build();
await host.StartAsync();

IStatesman statesman = host.Services.GetRequiredService<IStatesman>();
IState<UserServiceState> user = statesman.State(userState, "user-42");
IStateSnapshot<UserServiceState> loaded = await user.GetAsync(StateReadOptions.Fresh);
Console.WriteLine($"Loaded {loaded.RequiredValue.DisplayName} at revision {loaded.Revision}.");

IStateSnapshot<UserServiceState> enabled = await user.DispatchAsync(new SetEnabled(true));
Console.WriteLine($"Enabled: {enabled.RequiredValue.Enabled}; revision: {enabled.Revision}.");
Console.WriteLine(StatesmanManifestExporter.ToJson(statesman.Manifest));

await host.StopAsync();

[ManagedState]
internal sealed record UserServiceState(
    string DisplayName,
    IReadOnlyList<string> Permissions,
    bool Enabled);

internal sealed record UserProfile(string Id, string DisplayName);

internal sealed record SetEnabled(bool Enabled);

internal interface IUserService
{
    ValueTask<UserProfile> GetProfileAsync(string id, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<string>> GetPermissionsAsync(string id, CancellationToken cancellationToken);
}

internal sealed class DemoUserService : IUserService
{
    public ValueTask<UserProfile> GetProfileAsync(string id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new UserProfile(id, "Ada Lovelace"));

    public ValueTask<IReadOnlyList<string>> GetPermissionsAsync(string id, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>(new[] { "catalog.read", "catalog.write" });
}
