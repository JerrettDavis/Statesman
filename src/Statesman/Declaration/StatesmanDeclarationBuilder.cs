using System.Security.Cryptography;
using System.Text;

namespace Statesman;

public sealed class StatesmanDeclarationBuilder
{
    private readonly string _id;
    private readonly string _version;
    private readonly DeclarationDefaults _defaults = new();
    private readonly Dictionary<StatePath, ContainerRegistration> _containers = new();
    private readonly Dictionary<StatePath, IStateDefinitionRegistration> _states = new();
    private readonly Dictionary<string, string> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private bool _built;

    internal StatesmanDeclarationBuilder(string id, string version)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A Statesman declaration requires an id.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("A Statesman declaration requires a version.", nameof(version));
        }

        _id = id.Trim().ToLowerInvariant();
        _version = version.Trim();
        _containers[StatePath.Root] = new ContainerRegistration
        {
            Path = StatePath.Root,
            Isolation = StateContainerIsolation.Attached,
            Description = "Global state root",
        };
    }

    public StatesmanDeclarationBuilder Defaults(Action<StateDefaultsBuilder> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        configure(new StateDefaultsBuilder(_defaults));
        return this;
    }

    public StatesmanDeclarationBuilder Metadata(string key, string value)
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _metadata[key.Trim()] = value;
        return this;
    }

    public StatesmanDeclarationBuilder State<T>(
        StateKey<T> key,
        Action<StateDefinitionBuilder<T>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        AddState(key.Path, StatePath.Root, _defaults, configure);
        return this;
    }

    public StatesmanDeclarationBuilder State<T>(
        string path,
        Action<StateDefinitionBuilder<T>> configure) =>
        State(StateKey.Define<T>(path), configure);

    public StatesmanDeclarationBuilder Container(
        string path,
        Action<StateContainerBuilder> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        StatePath fullPath = Combine(StatePath.Root, path);
        ContainerRegistration registration = GetOrCreateContainer(fullPath);
        configure(new StateContainerBuilder(this, registration, _defaults.Clone()));
        return this;
    }

    public StatesmanDeclaration Build()
    {
        EnsureMutable();
        _built = true;

        var definitions = new List<IStateRuntimeDefinition>(_states.Count);
        foreach ((StatePath _, IStateDefinitionRegistration registration) in _states.OrderBy(pair => pair.Key))
        {
            definitions.Add(registration.Freeze());
        }

        StateDefinitionManifest[] states = definitions
            .Select(definition => definition.Manifest)
            .OrderBy(state => state.Path)
            .ToArray();

        StateContainerManifest[] containers = _containers.Values
            .OrderBy(container => container.Path)
            .Select(container => new StateContainerManifest
            {
                Path = container.Path,
                Isolation = container.Isolation,
                States = container.States.OrderBy(path => path).ToArray(),
                Tags = StatesmanCollections.ReadOnlySorted(container.Tags),
                Description = container.Description,
            })
            .ToArray();

        var provisional = new StatesmanManifest
        {
            Id = _id,
            Version = _version,
            Fingerprint = string.Empty,
            Containers = containers,
            States = states,
            Metadata = StatesmanCollections.ReadOnlySorted(_metadata),
        };

        string canonical = StatesmanManifestExporter.ToJson(provisional, indented: false);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        StatesmanManifest manifest = provisional with { Fingerprint = fingerprint };
        return new StatesmanDeclaration(manifest, definitions);
    }

    internal StateContainerBuilder AddContainer(
        StatePath parent,
        string path,
        DeclarationDefaults inheritedDefaults)
    {
        EnsureMutable();
        StatePath fullPath = Combine(parent, path);
        ContainerRegistration registration = GetOrCreateContainer(fullPath);
        return new StateContainerBuilder(this, registration, inheritedDefaults.Clone());
    }

    internal void AddState<T>(
        StatePath path,
        StatePath container,
        DeclarationDefaults defaults,
        Action<StateDefinitionBuilder<T>> configure)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(configure);
        if (path.IsRoot)
        {
            throw new StateDeclarationException("A state path cannot be empty.");
        }

        if (_states.ContainsKey(path))
        {
            throw new StateDeclarationException($"State '{path}' is declared more than once.");
        }

        ContainerRegistration owner = GetOrCreateContainer(container);
        if (!path.IsDescendantOf(container))
        {
            throw new StateDeclarationException(
                $"State '{path}' must be beneath its declaring container '{container}'.");
        }

        var registration = new StateDefinitionRegistration<T>(path, defaults);
        configure(new StateDefinitionBuilder<T>(registration));
        _states[path] = registration;
        owner.States.Add(path);
    }

    internal static StatePath Combine(StatePath parent, string relative)
    {
        StatePath child = new(relative);
        if (child.IsRoot)
        {
            throw new StateDeclarationException("A container or state path cannot be empty.");
        }

        return parent.IsRoot ? child : new StatePath($"{parent.Value}/{child.Value}");
    }

    private ContainerRegistration GetOrCreateContainer(StatePath path)
    {
        if (_containers.TryGetValue(path, out ContainerRegistration? existing))
        {
            return existing;
        }

        var registration = new ContainerRegistration
        {
            Path = path,
            Isolation = StateContainerIsolation.Attached,
        };
        _containers[path] = registration;
        return registration;
    }

    private void EnsureMutable()
    {
        if (_built)
        {
            throw new InvalidOperationException("A Statesman declaration builder can only build once.");
        }
    }
}

public sealed class StateContainerBuilder
{
    private readonly StatesmanDeclarationBuilder _root;
    private readonly ContainerRegistration _container;
    private readonly DeclarationDefaults _defaults;

    internal StateContainerBuilder(
        StatesmanDeclarationBuilder root,
        ContainerRegistration container,
        DeclarationDefaults defaults)
    {
        _root = root;
        _container = container;
        _defaults = defaults;
    }

    public StateContainerBuilder Isolated(bool enabled = true)
    {
        _container.Isolation = enabled
            ? StateContainerIsolation.Isolated
            : StateContainerIsolation.Attached;
        return this;
    }

    public StateContainerBuilder Attached()
    {
        _container.Isolation = StateContainerIsolation.Attached;
        return this;
    }

    public StateContainerBuilder Describe(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        _container.Description = description.Trim();
        return this;
    }

    public StateContainerBuilder Tag(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _container.Tags[key.Trim()] = value;
        return this;
    }

    public StateContainerBuilder Defaults(Action<StateDefaultsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(new StateDefaultsBuilder(_defaults));
        return this;
    }

    public StateContainerBuilder State<T>(
        string relativePath,
        Action<StateDefinitionBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        StatePath path = StatesmanDeclarationBuilder.Combine(_container.Path, relativePath);
        _root.AddState(path, _container.Path, _defaults, configure);
        return this;
    }

    public StateContainerBuilder State<T>(
        StateKey<T> key,
        Action<StateDefinitionBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _root.AddState(key.Path, _container.Path, _defaults, configure);
        return this;
    }

    public StateContainerBuilder Container(string relativePath, Action<StateContainerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        StateContainerBuilder child = _root.AddContainer(_container.Path, relativePath, _defaults);
        configure(child);
        return this;
    }
}
