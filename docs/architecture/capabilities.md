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
| `IStateLedgerReplica` | Yes | Yes | No | Yes | No |
| `IStateLeaseProvider` | No | No | Yes | Yes | No |
| `IStateChangeFeed` | Yes | Yes | Yes | Yes | Yes |
| `IPartitionCatalog` | Yes | Yes | Yes | Yes | Yes |
| `IDistributedCapture` | No | No | Yes | Yes | Yes |
| `IReplicationLagSource` | No | No | No | No | No |

`TieredStateLedgerStore` additionally implements `IStateCapabilityProvider`, forwarding capability
discovery to whichever of its hot/cold stores can back it (hot first). Its own matrix cells report
only capabilities it implements *directly* — `TryGetCapability` can still succeed on a `Tiered`
store at runtime via forwarding even where a cell above says "No".

Capabilities land across ROADMAP 0.3 phases and are added to this table, and to
`CapabilityMatrixTests`, as they ship: `IStateChangeNotifier`.
