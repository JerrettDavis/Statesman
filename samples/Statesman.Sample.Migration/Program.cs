using Statesman;

StateKey<SessionState> sessionKey = StateKey.Define<SessionState>("session/current");
StatesmanDeclaration declaration = global::Statesman.Statesman.Declare("legacy-bridge")
    .State(sessionKey, state => state
        .SuppressEquivalentWrites()
        .Retain(retention => retention.Last(100)))
    .Build();

var services = new EmptyServices();
await using StateStoreResolver stores = StateStoreResolver.InMemory();
await using IStatesman statesman = declaration.CreateRuntime(services, stores);
var legacy = new LegacySession();
var bridge = new LegacySessionBridge(legacy, statesman.State(sessionKey));

legacy.SetUser("user-42");
await bridge.FlushAsync();
Console.WriteLine(statesman.State(sessionKey).Current.RequiredValue.UserId);

[ManagedState]
internal sealed record SessionState(string? UserId, bool IsAuthenticated);

internal sealed class LegacySession
{
    public string? UserId { get; private set; }

    public event Action? Changed;

    public void SetUser(string? userId)
    {
        UserId = userId;
        Changed?.Invoke();
    }
}

[StateMutationBoundary("Temporary bridge while the legacy session is migrated.")]
internal sealed class LegacySessionBridge
{
    private readonly LegacySession _legacy;
    private readonly IState<SessionState> _state;
    private int _dirty;

    public LegacySessionBridge(LegacySession legacy, IState<SessionState> state)
    {
        _legacy = legacy;
        _state = state;
        _legacy.Changed += () => Interlocked.Exchange(ref _dirty, 1);
    }

    public async ValueTask FlushAsync()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        await _state.SetAsync(new SessionState(_legacy.UserId, _legacy.UserId is not null), new StateWriteOptions
        {
            Source = "legacy-session",
        });
    }
}

internal sealed class EmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
