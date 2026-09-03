# ADR 0005: Tiered storage keeps cold storage authoritative

## Status

Accepted.

## Decision

A tiered store appends and reads history through the cold provider. The hot provider must implement `IStateLedgerReplica`, and accepted cold records are imported with their exact revision and global position.

Head reads validate cold storage by default and repair hot state. `PreferHot` is an explicit eventual-consistency mode. Serving hot data when cold is unavailable is separately opt-in.

## Consequences

Cache failure cannot reverse a successful authoritative append. The hot layer never invents a competing lineage. Default reads cost a cold lookup, while deployments that tolerate lag may intentionally choose hot preference.
