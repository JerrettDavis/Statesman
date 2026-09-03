namespace Statesman;

/// <summary>Marks a type whose authoritative mutations should flow through Statesman.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
public sealed class ManagedStateAttribute : Attribute
{
}

/// <summary>Marks an intentional adapter, reducer, or migration boundary where direct mutation is permitted.</summary>
[AttributeUsage(
    AttributeTargets.Class |
    AttributeTargets.Struct |
    AttributeTargets.Method |
    AttributeTargets.Constructor,
    Inherited = true)]
public sealed class StateMutationBoundaryAttribute : Attribute
{
    public StateMutationBoundaryAttribute(string? reason = null)
    {
        Reason = reason;
    }

    public string? Reason { get; }
}

/// <summary>Suppresses Statesman mutation analysis for a narrowly scoped symbol.</summary>
[AttributeUsage(
    AttributeTargets.Class |
    AttributeTargets.Struct |
    AttributeTargets.Property |
    AttributeTargets.Field |
    AttributeTargets.Method |
    AttributeTargets.Constructor,
    Inherited = true)]
public sealed class StateMutationAnalysisIgnoreAttribute : Attribute
{
    public StateMutationAnalysisIgnoreAttribute(string reason)
    {
        Reason = reason;
    }

    public string Reason { get; }
}
