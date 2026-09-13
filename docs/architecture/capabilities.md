# Distributed capability matrix

Every distributed or optional capability in Statesman is expressed as a marker interface
extending `IStateCapability` (`src/Statesman.Abstractions/Ledger.cs`). A store implements a
capability interface only when it can honestly back the guarantee it implies — a store that
cannot simply does not implement it. Callers discover support with `TryGetCapability`:

```csharp
if (store.TryGetCapability(out IStateLedgerReplica? replica))
{
    await replica.ImportAsync(record, cancellationToken);
}
```

This table is asserted against reflection by
`tests/Statesman.Capabilities.Tests/CapabilityMatrixTests.cs` — it cannot drift from the code
without that test failing.

| Capability | InMemory | FileSystem | Redis | EntityFrameworkCore | Tiered |
|---|---|---|---|---|---|
| `IStateLedgerReplica` | Yes | Yes | Yes | Yes | No |
| `IStateLeaseProvider` | No | No | Yes | Yes | No |
| `IStateChangeFeed` | Yes | Yes | Yes | Yes | Yes |
| `IPartitionCatalog` | Yes | Yes | Yes | Yes | Yes |
| `IDistributedCapture` | No | No | Yes | Yes | Yes |
| `IReplicationLagSource` | No | No | No | No | Yes |
| `IStateChangeNotifier` | Yes | No | Yes | No | Yes |

`TieredStateLedgerStore` additionally implements `IStateCapabilityProvider`, forwarding capability
discovery to whichever of its hot/cold stores can back it (hot first) — except `IStateLedgerReplica`,
which is never forwarded: the hot replica is the tiered store's private cache-repair channel and the
cold store is authoritative, so an exact import (a `Statesman.Tooling` restore, for example) must
target the cold store directly.

**Its own matrix cells report only capabilities it implements *directly*, and a cell is what the type
declares rather than what a particular instance can do.** `TryGetCapability` can still succeed on a
`Tiered` store at runtime via forwarding where a cell above says "No" — and, since ROADMAP 0.3
Phase 17, it can also answer **"No" where a cell says "Yes"**: the five capabilities `Tiered` declares
are all pure delegation, so it answers for one only when the tier behind it can back the guarantee
(cold for `IStateChangeFeed`, `IPartitionCatalog`, `IStateChangeNotifier` and `IDistributedCapture`;
both tiers' `IPartitionCatalog` for `IReplicationLagSource`). A tiered store over an in-memory cold
tier therefore reports `IDistributedCapture` as unavailable, which is the honest answer, rather than
reporting it available and throwing `NotSupportedException` at first use. More generally, a store that
implements `IStateCapabilityProvider` answers discovery for itself and its answer is final; a store
that does not is answered by a direct cast, exactly as before.

Every capability shipped by ROADMAP 0.3 is now in this table. A new capability must be added here and
to `CapabilityMatrixTests` in the same change that introduces the interface — the completeness half
of that test reflects over every `IStateCapability` implementer and fails until both land.
