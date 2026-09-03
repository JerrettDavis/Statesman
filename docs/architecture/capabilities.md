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
| `IStateLeaseProvider` | No | No | Yes | No | No |

Capabilities land across ROADMAP 0.3 phases and are added to this table, and to
`CapabilityMatrixTests`, as they ship: `IStateChangeFeed`, `IStateChangeNotifier`,
`IPartitionCatalog`, `IDistributedCapture`, `IReplicationLagSource`.
