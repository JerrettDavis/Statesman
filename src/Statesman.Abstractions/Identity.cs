using System.Text;

namespace Statesman;

/// <summary>Canonical, slash-delimited identity for a declared state.</summary>
public readonly record struct StatePath : IComparable<StatePath>
{
    private readonly string? _value;

    public static StatePath Root { get; } = default;

    public StatePath(string value)
        : this(Normalize(value), skipValidation: true)
    {
    }

    private StatePath(string value, bool skipValidation)
    {
        _ = skipValidation;
        _value = value.Length == 0 ? null : value;
    }

    public string Value => _value ?? string.Empty;

    public bool IsRoot => Value.Length == 0;

    public StatePath Child(string segment)
    {
        string normalized = Normalize(segment);
        if (normalized.Length == 0 || normalized.Contains('/'))
        {
            throw new ArgumentException("A child segment must contain exactly one non-empty path segment.", nameof(segment));
        }

        return IsRoot ? new StatePath(normalized, true) : new StatePath($"{Value}/{normalized}", true);
    }

    public bool IsDescendantOf(StatePath parent) =>
        parent.IsRoot ||
        Value.StartsWith(parent.Value + "/", StringComparison.OrdinalIgnoreCase);

    public int CompareTo(StatePath other) => StringComparer.OrdinalIgnoreCase.Compare(Value, other.Value);

    public override string ToString() => Value;

    public static implicit operator StatePath(string value) => new(value);

    public static explicit operator string(StatePath value) => value.Value;

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string candidate = value.Trim().Replace('\\', '/').Trim('/');
        var output = new StringBuilder(candidate.Length);
        bool lastWasSlash = false;

        foreach (char character in candidate)
        {
            if (character == '/')
            {
                if (!lastWasSlash)
                {
                    output.Append('/');
                }

                lastWasSlash = true;
                continue;
            }

            if (char.IsControl(character))
            {
                throw new ArgumentException("State paths cannot contain control characters.", nameof(value));
            }

            output.Append(char.ToLowerInvariant(character));
            lastWasSlash = false;
        }

        return output.ToString().Trim('/');
    }
}

/// <summary>Identifies one partition of a declared state.</summary>
public readonly record struct StatePartition
{
    private readonly string? _value;

    public static StatePartition Default { get; } = default;

    public StatePartition(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A partition cannot be empty.", nameof(value));
        }

        string candidate = value.Trim();
        if (candidate.Any(char.IsControl))
        {
            throw new ArgumentException("A partition cannot contain control characters.", nameof(value));
        }

        _value = string.Equals(candidate, "default", StringComparison.Ordinal) ? null : candidate;
    }

    public string Value => _value ?? "default";

    public override string ToString() => Value;

    public static implicit operator StatePartition(string value) => new(value);
}

/// <summary>Fully qualified runtime address of one state partition.</summary>
public readonly record struct StateAddress(string Root, StatePath Path, StatePartition Partition)
{
    public string Canonical => $"{(Root ?? string.Empty).Trim().ToLowerInvariant()}::{Path.Value}::{Partition.Value}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Root))
        {
            throw new ArgumentException("A state address requires a root.", nameof(Root));
        }

        if (Path.IsRoot)
        {
            throw new ArgumentException("A state address requires a non-root state path.", nameof(Path));
        }
    }

    public override string ToString() => Canonical;
}

/// <summary>Non-generic identity used for dynamic inspection and atomic captures.</summary>
public readonly record struct StateReference(StatePath Path, StatePartition Partition)
{
    public StateReference(StatePath path)
        : this(path, StatePartition.Default)
    {
    }
}

public interface IStateKey
{
    StatePath Path { get; }

    Type ValueType { get; }
}

/// <summary>Compile-time-safe identity for a state value.</summary>
public readonly record struct StateKey<T>(StatePath Path) : IStateKey
{
    public Type ValueType => typeof(T);

    public StateReference At(StatePartition partition) => new(Path, partition);

    public override string ToString() => $"{Path.Value}<{typeof(T).Name}>";
}

public static class StateKey
{
    public static StateKey<T> Define<T>(string path) => new(new StatePath(path));

    public static bool TryDefine<T>(string? path, out StateKey<T> key)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            key = default;
            return false;
        }

        key = Define<T>(path);
        return true;
    }
}
