using Microsoft.CodeAnalysis;

namespace Statesman.Analyzers;

internal static class DiagnosticDescriptors
{
    private const string Category = "Statesman";

    public static readonly DiagnosticDescriptor DirectMutation = new(
        id: "STM001",
        title: "Managed state is mutated outside Statesman",
        messageFormat: "'{0}' belongs to managed state '{1}' and should be changed through IState<T>, an interaction reducer, or an explicit StateMutationBoundary",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Direct mutation creates a second source of truth that bypasses the Statesman ledger and observers.");

    public static readonly DiagnosticDescriptor MutableShape = new(
        id: "STM002",
        title: "Managed state exposes a public mutation surface",
        messageFormat: "Managed state member '{0}' is publicly mutable; prefer init-only properties, records, or an intentional migration boundary",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Immutable state values make authoritative writes and analyzer enforcement reliable.");

    public static readonly DiagnosticDescriptor DynamicStateKey = new(
        id: "STM003",
        title: "State keys should be deterministic",
        messageFormat: "StateKey.Define<T> should receive a compile-time constant path so the declaration manifest is stable",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Dynamic state paths prevent deterministic manifests and reliable tooling.");
}
