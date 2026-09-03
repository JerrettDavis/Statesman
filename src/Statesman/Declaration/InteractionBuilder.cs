namespace Statesman;

public sealed class StateInteractionBuilder<TState, TCommand>
{
    private readonly string _name;
    private readonly List<Func<TState, TCommand, StateInteractionContext<TState, TCommand>, CancellationToken, ValueTask<string?>>> _requirements = new();
    private Func<TState, TCommand, StateInteractionContext<TState, TCommand>, CancellationToken, ValueTask<TState>>? _reducer;
    private string _description = string.Empty;

    internal StateInteractionBuilder(string name)
    {
        _name = name;
    }

    public StateInteractionBuilder<TState, TCommand> Describe(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _description = description.Trim();
        return this;
    }

    public StateInteractionBuilder<TState, TCommand> Require(
        Func<TState, TCommand, bool> requirement,
        string rejection)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejection);
        _requirements.Add((state, command, _, _) =>
            ValueTask.FromResult<string?>(requirement(state, command) ? null : rejection));
        return this;
    }

    public StateInteractionBuilder<TState, TCommand> Require(
        Func<TState, TCommand, StateInteractionContext<TState, TCommand>, CancellationToken, ValueTask<string?>> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        _requirements.Add(requirement);
        return this;
    }

    public StateInteractionBuilder<TState, TCommand> Reduce(Func<TState, TCommand, TState> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        _reducer = (state, command, _, _) => ValueTask.FromResult(reducer(state, command));
        return this;
    }

    public StateInteractionBuilder<TState, TCommand> Reduce(
        Func<TState, TCommand, StateInteractionContext<TState, TCommand>, TState> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        _reducer = (state, command, context, _) => ValueTask.FromResult(reducer(state, command, context));
        return this;
    }

    public StateInteractionBuilder<TState, TCommand> ReduceAsync(
        Func<TState, TCommand, StateInteractionContext<TState, TCommand>, CancellationToken, ValueTask<TState>> reducer)
    {
        ArgumentNullException.ThrowIfNull(reducer);
        _reducer = reducer;
        return this;
    }

    internal IStateInteraction<TState> Build()
    {
        if (_reducer is null)
        {
            throw new StateDeclarationException(
                $"Interaction '{_name}' for '{typeof(TState).FullName}' does not define a reducer.");
        }

        return new StateInteraction<TState, TCommand>(_name, _description, _requirements.ToArray(), _reducer);
    }
}
