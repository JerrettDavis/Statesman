# Outbox delivery

`Statesman.Outbox` reads a store's change feed from a persisted cursor and delivers each record to a message broker at least once. Observation (`StateChangeHub`) stays a separate, lossy, in-process concern from delivery — per [ADR 0004](../adr/0004-observation-is-not-delivery.md) — so a consumer that must not miss a change reads the outbox, never the observation stream.

## What "delivered" means

The dispatcher's promise, exactly:

> Every record this outbox reads from the feed is handed to the sink at least once, and the persisted cursor never advances past a record the sink has not accepted.

That is a promise about the dispatch loop, not about the ledger, and the two are not the same thing. Composed with the feed the outbox reads from, it becomes at-least-once delivery of every record *the feed yields*. The feed is lossless within retention on every provider — a consumer resuming from the cursor of the last record it accepted is never skipped past a record — so that is narrower than "every committed change is delivered" for one reason the outbox inherits and cannot fix, plus one provider-specific residual:

- **Prune loss.** `StateHandle` prunes after every successful append, so on the filesystem provider or Entity Framework Core with any non-default retention policy a record can leave the feed's own storage milliseconds after being written, before the outbox ever reads it. Redis and the in-memory provider do not trim their change-feed structures at all, so retention costs them unbounded feed growth rather than delivered records.
- **The filesystem crash window.** A crash between the record write and the change-log append leaves that record durably stored but permanently absent from the feed. This is a durability gap in one provider, not a property of the feed contract.

**Any provider with `StateRetentionPolicy.KeepAll` is a complete configuration**, with the filesystem crash window above as the one residual. See the providers page's [change feed semantics and limitations](../providers/index.md#change-feed-semantics-and-limitations) for the per-provider mechanism, and its [outbox delivery semantics and limitations](../providers/index.md#outbox-delivery-semantics-and-limitations) for the per-provider breakdown.

## Getting started

```csharp
services.AddStatesmanOutbox(
    options =>
    {
        options.OutboxId = "orders-outbox";
        options.StoreName = "relational";
        options.Root = "orders";
    },
    sink: _ => new InMemoryStateChangeSink(),
    cursors: provider => new FileSystemOutboxCursorStore(
        Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, "outbox-cursors")));
```

`AddStatesmanOutbox` registers a hosted worker that polls `StoreName`'s change feed on `OutboxOptions.PollInterval` and publishes to the sink. Swap `InMemoryStateChangeSink` for a real destination and `FileSystemOutboxCursorStore` for durable cursor storage in production — the in-memory cursor store used when `cursors` is omitted loses its position on every restart.

For Redis, `Statesman.Outbox.Redis` wires a stream sink and a Redis cursor store off one connection:

```csharp
services.AddStatesmanRedisOutbox(
    options =>
    {
        options.OutboxId = "orders-outbox";
        options.StoreName = "shared";
        options.Root = "orders";
    },
    connection: provider => provider.GetRequiredService<IConnectionMultiplexer>(),
    configureSink: sink => sink.StreamName = "orders.state-change",
    configureCursors: cursors => cursors.KeyPrefix = "orders");
```

## Single dispatcher, and why the lease is required by default

`OutboxOptions.RequireLease` defaults to `true`. A store with no `IStateLeaseProvider` — the in-memory and filesystem providers — is refused by name when the dispatcher is constructed, before anything runs. The reason is not caution for its own sake: two unleased dispatchers over one store do not merely double-publish, they race the cursor store, and a cursor write from one can advance the stored position past records the other has not yet published. That is a permanent, silent loss, and at-least-once delivery must not permit it.

Set `RequireLease = false` to run unleased anyway. This is a documented degradation, safe only when exactly one process will ever dispatch this outbox, and the hosted worker logs a warning once at start naming the store and outbox id. Redis and Entity Framework Core are the only shipped providers that implement `IStateLeaseProvider`, so an outbox over the filesystem or in-memory provider needs `RequireLease = false` or a store-supplied lease provider of its own. The outbox worker is this repository's first production caller of `IStateLease.RenewAsync` — it renews the lease while draining a long feed rather than letting it lapse mid-cycle. `OutboxOptions.LeaseRenewInterval` governs that cadence and defaults to a third of `LeaseTtl` when left null; renewal is tracked against the time since the last successful renewal (or acquisition), not against how long any one batch took, so it fires on schedule regardless of `BatchSize` or how fast the sink is.

## Cursors

An `IOutboxCursorStore` advances monotonically: a write at or below the stored position is a no-op, never a regression. `ReadAsync` returns `null` before the first write — never a zero position, which the cursor type itself forbids — and a fresh outbox reads the entire retained backlog on its first cycle.

Cursors are keyed by outbox id *alone*: two outboxes reading the same store need distinct `OutboxId` values, and pointing one outbox id at two different stores corrupts its resume point, since positions from different stores are unrelated. `AddStatesmanOutbox` throws `InvalidOperationException` if the same `OutboxId` is registered twice in one service collection. `InMemoryOutboxCursorStore` is correct for tests and single-process development only — it loses its position on restart, so the outbox re-delivers its entire backlog after every process restart. `FileSystemOutboxCursorStore` persists to one JSON file per outbox id, written atomically (temp file, then move), and enforces monotonicity under a gate keyed by the resolved file path shared across instances in one process — it is not safe for multiple processes writing a shared network directory, which is exactly what `RequireLease = true` exists to prevent by keeping only one process dispatching at a time.

A tiered store reads its change feed from its cold tier and takes its lease hot-first (per the tiering forwarding policy — see the conventions in `docs/superpowers/HANDOVER.md`), so key the outbox's cursor by the **tiered** store's name, not its cold store's name — the two are different `IStateLedgerStore.Name` values and the cursor store has no way to know they are related.

Entity Framework Core cursor storage is deliberately not shipped in this phase, so no existing Entity Framework Core consumer needs a second migration this soon after `StatesmanLeases`. Use the filesystem or Redis cursor store against an Entity Framework Core-backed outbox, or supply your own `IOutboxCursorStore`.

## The message

Every record the dispatcher reads is flattened into a `StateChangeMessage` stamped `statesman.state-change/v1`:

| Field | Meaning |
|---|---|
| `MessageId` | The semantic deduplication key: `{Store}/{Address}#{Revision}`. Unique by construction — one store never produces two records with the same canonical address and revision. |
| `Store` | The `IStateLedgerStore.Name` the record was read from. |
| `Root`, `Path`, `Partition`, `Address` | The record's address, `Address` being the canonical (lower-cased root) form. |
| `Revision` | The record's revision within its address. |
| `GlobalPosition` | The record's position in the store's global order — the *transport* deduplication key, unique within one store's position lineage only. |
| `OccurredAt`, `Operation`, `Status`, `ValueType`, `SchemaVersion`, `Source` | Carried verbatim from the `StateRecord`. |
| `Fingerprint` | The declaration fingerprint, populated from `OutboxOptions.Fingerprint` (or the root's manifest when `Root` is set and `Fingerprint` is not) — no store persists this itself. |
| `ContentType` | The media type declared for `Payload`, from `OutboxOptions.PayloadContentType`. Null exactly when there is no payload. |
| `Payload` | The serialized value, carried opaquely and never deserialized by the outbox — a store's `IStateSerializer` is pluggable, so the bytes are not necessarily JSON. |
| `CorrelationId`, `CausationId`, `Metadata`, `Error` | Carried from the record. |

`MessageId` is the key to dedupe on for exactly-once processing: it is a semantic identity that survives export and restore, unlike `GlobalPosition`, which is meaningless across stores. `GlobalPosition` is carried as a JSON number and is exact for a `long`, but a consumer whose JSON parser represents numbers as doubles loses precision above 2^53 — key on `MessageId` rather than `GlobalPosition` in any consumer whose parser you do not control.

Two fields are deliberately absent from the wire: `FreshUntil` and `ServeUntil`. They are cache-policy fields meaningful only to the runtime that wrote them, not facts about the change itself, so they are not carried.

## Redis Streams

`Statesman.Outbox.Redis`'s `RedisStreamStateChangeSink` writes to the stream key `{KeyPrefix}:{StreamName}` with one `XADD` per message, using the explicit entry id `{GlobalPosition}-0` rather than Redis's auto-generated id. Redis refuses an `XADD` whose id is not greater than the stream's current top entry, so re-publishing a batch after a crash is rejected server-side as a duplicate rather than actually duplicated. On that rejection the sink reads the entry already at that id and compares its `messageId` field to the message being published — equal counts on `Deduplicated` and the batch continues; any other Redis error, and a missing or mismatched entry, propagates as an `InvalidOperationException` rather than silently dropping the message, so the dispatcher does not advance its cursor. Stream entry ids are two unsigned 64-bit integers, so every `GlobalPosition` fits exactly, including the filesystem provider's UTC-tick positions — this is unrelated to, and not bounded by, `RedisStateLedgerStore.MaxImportablePosition` (2^52), which is about sorted-set scores in the ledger's own change feed, not stream entry ids.

**A stream key must be exclusive to one store's position lineage.** `GlobalPosition` is unique only within one store's own history, and `KeyPrefix`/`StreamName` default to the same constants for every registration, so two `AddStatesmanRedisOutbox` calls over two different stores publish into the same key unless at least one overrides `configureSink`. Give each store's outbox a distinct `StreamName` or `KeyPrefix` — re-pointing an outbox at a store with a different (or restarted) position lineage needs the same care.

Consume the stream with `XREAD` for a single reader or a consumer group for competing consumers. Because delivery is at-least-once and the sink only deduplicates against the stream's current top entry, a consumer that must be exactly-once should key on the `messageId` field rather than relying on the stream's own dedup.

Set `MaxLength` to trim the stream, but do so deliberately: trimming discards entries a slow consumer group may not have read yet, and the outbox has no way to know whether every consumer has caught up before a trim removes them.

## Failure handling

The dispatcher publishes a batch to the sink first and persists the cursor only after the sink accepts it. A batch the sink rejects blocks the cursor at its position and the hosted worker retries the same batch on the next poll, backing off exponentially between `MinRetryDelay` and `MaxRetryDelay` (which must stay below `LeaseTtl`, so a stalled worker releases its lease rather than holding it while doing nothing).

`SkipPoisonAfterAttempts` is opt-in and null by default, meaning a poison batch blocks forever rather than being silently dropped. When set, a batch that fails that many consecutive attempts is skipped: the cursor advances past it, the dispatch result reports how many messages were skipped, and the hosted worker logs an error naming how many messages were never delivered.

## First run against an existing store

The change feed has no paging: a first run against a store with existing history pulls the entire backlog into one `ReadAsync` call, materialized in memory before the first record is yielded, on the Redis and in-memory providers. `BatchSize` bounds how many messages are handed to the sink in one `PublishAsync` call — it does not bound what the feed itself materializes.

There is deliberately no "start from head" option in this version, because it would silently skip history a consumer might expect to see. If starting from the current tail is genuinely what you want, seed the cursor store directly with the store's current position before the outbox's first cycle runs.
