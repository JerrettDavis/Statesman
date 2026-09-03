namespace Statesman;

internal sealed class StatesmanRegistry : IStatesmanRegistry
{
    private readonly IReadOnlyDictionary<string, IStatesman> _roots;

    public StatesmanRegistry(IEnumerable<IStatesman> statesmen)
    {
        IStatesman[] all = statesmen.ToArray();
        string[] duplicateIds = all
            .GroupBy(statesman => statesman.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateIds.Length > 0)
        {
            throw new StateDeclarationException(
                $"Multiple Statesman roots use the same id: {string.Join(", ", duplicateIds)}.");
        }

        _roots = all.ToDictionary(statesman => statesman.Id, StringComparer.OrdinalIgnoreCase);
        All = all;
    }

    public IReadOnlyCollection<IStatesman> All { get; }

    public IStatesman Get(string id) => TryGet(id, out IStatesman? statesman)
        ? statesman!
        : throw new KeyNotFoundException(
            $"No Statesman root named '{id}' is registered. Available roots: {string.Join(", ", _roots.Keys.Order(StringComparer.OrdinalIgnoreCase))}.");

    public bool TryGet(string id, out IStatesman? statesman) => _roots.TryGetValue(id, out statesman);
}
