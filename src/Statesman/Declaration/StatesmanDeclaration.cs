namespace Statesman;

public sealed class StatesmanDeclaration
{
    private readonly IReadOnlyDictionary<StatePath, IStateRuntimeDefinition> _definitions;

    internal StatesmanDeclaration(StatesmanManifest manifest, IEnumerable<IStateRuntimeDefinition> definitions)
    {
        Manifest = manifest;
        _definitions = definitions.ToDictionary(definition => definition.Manifest.Path);
    }

    public StatesmanManifest Manifest { get; }

    public IStatesman CreateRuntime(
        IServiceProvider services,
        IStateStoreResolver stores,
        IStateSerializer? serializer = null,
        TimeProvider? timeProvider = null) =>
        new StatesmanRuntime(
            this,
            services,
            stores,
            serializer ?? new JsonStateSerializer(),
            timeProvider ?? TimeProvider.System);

    internal IReadOnlyDictionary<StatePath, IStateRuntimeDefinition> Definitions => _definitions;
}
