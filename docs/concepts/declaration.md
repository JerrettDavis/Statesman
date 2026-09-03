# The declaration DSL

The declaration is the static truth about a Statesman root. It answers six questions before runtime code executes:

1. What state exists?
2. How is each state identified and partitioned?
3. Where may its value come from?
4. How may it change?
5. What freshness, fault, retention, and storage policies apply?
6. Which architectural boundary owns it?

## Defaults and override order

Defaults are copied when a state or child container is declared. Later changes to a parent do not retroactively rewrite children. This makes declaration order deterministic and prevents hidden mutation of already composed state definitions.

```csharp
Statesman.Declare("catalog")
    .Defaults(defaults => defaults
        .StoreWith("shared")
        .Freshness(policy => policy.FreshFor(TimeSpan.FromMinutes(5)))
        .Retain(policy => policy.Last(100)))
    .Container("products", products => products
        .Defaults(defaults => defaults.StoreWith("local"))
        .State<ProductState>("by-id", state => state.Partitioned()))
    .Build();
```

The effective order is root defaults, inherited container defaults, child container overrides, and state overrides.

## State definition surface

A state can declare:

- singleton or partitioned identity
- schema version and migrations
- initial value or factory
- named ledger store
- freshness and stale serving window
- proactive sources and source failure mode
- refresh triggers
- typed interactions
- invariants and equality comparer
- equivalent-write suppression
- last-known-value fault behavior
- history retention
- description and tags

All runtime-relevant settings are represented in the manifest except executable delegates. Delegate identities are represented by their declared source or interaction names and static types.

## Deterministic manifests

`Build()` sorts state paths, container paths, tags, source order, interactions, warm partitions, and signals. It serializes a canonical manifest with an empty fingerprint, computes SHA-256 over that representation, then returns the immutable manifest with the fingerprint populated.

The fingerprint is useful for:

- startup diagnostics
- environment comparison
- CI drift checks
- remote capability negotiation
- generated documentation
- support bundles

It is not a security signature. Sign or attest the exported manifest separately when provenance matters.

## Export

```csharp
string json = StatesmanManifestExporter.ToJson(declaration.Manifest);
string mermaid = StatesmanManifestExporter.ToMermaid(declaration.Manifest);
```

The Mermaid output shows roots, nested container boundaries, states, types, partitioning, and proactive sources.
