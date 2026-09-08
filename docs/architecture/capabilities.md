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
target the cold store directly. Its own matrix cells report
only capabilities it implements *directly* — `TryGetCapability` can still succeed on a `Tiered`
store at runtime via forwarding even where a cell above says "No".

Every capability shipped by ROADMAP 0.3 is now in this table. A new capability must be added here and
to `CapabilityMatrixTests` in the same change that introduces the interface — the completeness half
of that test reflects over every `IStateCapability` implementer and fails until both land.
