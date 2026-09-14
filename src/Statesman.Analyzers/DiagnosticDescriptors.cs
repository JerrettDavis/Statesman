using Microsoft.CodeAnalysis;

namespace Statesman.Analyzers;

internal static class DiagnosticDescriptors
{
    private const string Category = "Statesman";

    // The published analyzer guide has one "##" section per rule, and docfx turns each heading into
    // the anchor named below. RS2008, the analyzer-release-tracking rule, is in this project's
    // NoWarn, so a help link is the only discoverability surface a light bulb has.
    private const string HelpBase = "https://jerrettdavis.github.io/Statesman/guides/analyzers.html#";

    public static readonly DiagnosticDescriptor DirectMutation = new(
        id: "STM001",
        title: "Managed state is mutated outside Statesman",
        messageFormat: "'{0}' belongs to managed state '{1}' and should be changed through IState<T>, an interaction reducer, or an explicit StateMutationBoundary",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Direct mutation creates a second source of truth that bypasses the Statesman ledger and observers.",
        helpLinkUri: HelpBase + "stm001-direct-managed-state-mutation");

    public static readonly DiagnosticDescriptor MutableShape = new(
        id: "STM002",
        title: "Managed state exposes a public mutation surface",
        messageFormat: "Managed state member '{0}' is publicly mutable; prefer init-only properties, records, or an intentional migration boundary",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Immutable state values make authoritative writes and analyzer enforcement reliable.",
        helpLinkUri: HelpBase + "stm002-publicly-mutable-managed-state-shape");

    public static readonly DiagnosticDescriptor DynamicStateKey = new(
        id: "STM003",
        title: "State keys should be deterministic",
        messageFormat: "StateKey.Define<T> should receive a compile-time constant path so the declaration manifest is stable",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Dynamic state paths prevent deterministic manifests and reliable tooling.",
        helpLinkUri: HelpBase + "stm003-dynamic-state-key");

    public static readonly DiagnosticDescriptor CollectionMutation = new(
        id: "STM004",
        title: "Managed state's collection is mutated outside Statesman",
        messageFormat: "'{0}' mutates the collection held by managed state '{1}' and should be changed through IState<T>, an interaction reducer, or an explicit StateMutationBoundary",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A collection reached through managed state is still managed state; mutating it in place creates a second source of truth that bypasses the Statesman ledger and observers.",
        helpLinkUri: HelpBase + "stm004-managed-state-collection-mutation");
}
