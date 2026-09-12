# Entity Framework Core migrations

Statesman's two Entity Framework Core packages — `Statesman.Persistence.EntityFrameworkCore` (the
ledger) and `Statesman.Outbox.EntityFrameworkCore` (the outbox cursor) — are provider-neutral and ship
no migration. ROADMAP 0.3 Phase 14 adds six small, opt-in packages that each ship exactly one
generated `Initial` migration for one context, on one engine. Owning your migration yourself, by
subclassing the base context, stays the supported default. Nothing on this page changes that; it
documents the alternative.

## What ships, and what does not

Six packages, one `Initial` migration each:

| Package | Extension method |
|---|---|
| `Statesman.Persistence.EntityFrameworkCore.Sqlite` | `UseStatesmanLedgerSqliteMigrations()` |
| `Statesman.Persistence.EntityFrameworkCore.SqlServer` | `UseStatesmanLedgerSqlServerMigrations()` |
| `Statesman.Persistence.EntityFrameworkCore.PostgreSQL` | `UseStatesmanLedgerPostgreSqlMigrations()` |
| `Statesman.Outbox.EntityFrameworkCore.Sqlite` | `UseStatesmanOutboxSqliteMigrations()` |
| `Statesman.Outbox.EntityFrameworkCore.SqlServer` | `UseStatesmanOutboxSqlServerMigrations()` |
| `Statesman.Outbox.EntityFrameworkCore.PostgreSQL` | `UseStatesmanOutboxPostgreSqlMigrations()` |

Each is opt-in: a consumer who never calls one of these methods sees exactly the behaviour these
packages had before they existed. No migration is discovered, none is applied, and nothing here is
required to use either core package.

## Registering a shipped migration

Three calls, in this order — the provider, then the migrations extension, then `Migrate()` or
`MigrateAsync()`:

```csharp
services.AddDbContextFactory<AppStateDbContext>(options =>
    options.UseSqlite(connectionString).UseStatesmanLedgerSqliteMigrations());

// At startup, once:
await using var context = await factory.CreateDbContextAsync();
await context.Database.MigrateAsync();
```

The migrations call comes **after** the provider call: `UseStatesmanLedgerSqliteMigrations()` reads
the `RelationalOptionsExtension` the provider call already configured off the builder, re-adds it with
the migrations assembly and history table name applied, and replaces `IMigrationsAssembly`. Calling it
before a provider has been selected throws `InvalidOperationException` rather than silently doing
nothing.

The outbox packages follow the identical shape, against `StatesmanOutboxCursorDbContext` and
`UseStatesmanOutboxSqliteMigrations()` / `UseStatesmanOutboxSqlServerMigrations()` /
`UseStatesmanOutboxPostgreSqlMigrations()`.

## The subclass rule, and why the package can honour it

Every shipped migration targets the **base** context — `StatesmanLedgerDbContext` or
`StatesmanOutboxCursorDbContext` — never a consumer's subclass, because a subclass's exact type is
not known when the migration is generated. Entity Framework Core discovers migrations by exact
`[DbContext]` type equality, so without help a migration generated against the base context is
invisible to a subclass: `Database.Migrate()` would return normally having created only the history
table, and the shipped tables would simply not exist.

Each of the six packages installs a subclass-tolerant `IMigrationsAssembly` replacement that relaxes
that check to `declared.IsAssignableFrom(runningContextType)`, so a derived context still gets the
shipped migration applied. This is what lets every existing consumer — which subclasses the base
context, as every test and every other documentation page in this repository does — take a shipped
package with no other change.

**A subclass that adds to the model is rejected, loudly, by Entity Framework Core itself:**

```
InvalidOperationException: An error was generated for warning
'Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning': The model for context
'AppDbContext' has pending changes. Add a new migration before updating the database.
```

The subclass-tolerant assembly makes the shipped migration reach the subclass; it does not — and must
not — let a subclass silently add tables or columns the shipped migration knows nothing about. A
consumer who genuinely wants shipped tables beside their own suppresses this with
`ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))` and owns the
consequence: with the warning ignored, `Migrate()` proceeds without applying anything for the
subclass's own additions, so those additions are the consumer's responsibility to migrate themselves.

## Adopting a migration for a database created by `EnsureCreated`

A database that was created with `EnsureCreated()` has no migration-history row, so pointing it at a
shipped migration and calling `Migrate()` fails on the very first table:

```
Microsoft.Data.Sqlite.SqliteException: SQLite Error 1: 'table "StatesmanHeads" already exists'.
```

Both core packages ship `BaselineAsync(DbContext, CancellationToken = default)` for exactly this case
— `Statesman.StatesmanLedgerMigrations.BaselineAsync` and
`Statesman.Outbox.EntityFrameworkCore.StatesmanOutboxMigrations.BaselineAsync`. It writes the
migrations-history table and one history row per shipped migration id, inside one transaction run
through the context's own execution strategy, without touching any other table. It returns `bool`:
`true` when it wrote the history rows, `false` when the history already had rows (a no-op), so it is
safe to call unconditionally at startup. It throws `InvalidOperationException` when no shipped
migration was discovered at all, because that means the options are misconfigured and silently writing
an empty history table would bake the misconfiguration in.

The history row it writes records `ProductInfo.GetVersion()` — the running Entity Framework Core
assembly's version — the same source `Migrator.ApplyMigration` uses when it writes a history row for a
migration applied through `Migrate()`. A baselined row is therefore indistinguishable from one
`Migrate()` would have written itself.

**Baselining an empty database is a mistake, not a shortcut.** `BaselineAsync` does not check whether
the tables it is baselining actually exist — it trusts the caller. Running it against a database that
was never `EnsureCreated`-initialized tells Entity Framework Core the shipped tables are already
present when they are not, and every later read or write against them fails. Call it only against a
database you know was created by `EnsureCreated`, immediately before the first `Migrate()` call.

## The two history table names

A shipped migration's history table is not the default `__EFMigrationsHistory`:

| Context | History table |
|---|---|
| `StatesmanLedgerDbContext` | `__StatesmanLedgerMigrationsHistory` |
| `StatesmanOutboxCursorDbContext` | `__StatesmanOutboxMigrationsHistory` |

Two names, one per context, because the ledger and the outbox cursor are independently adoptable and a
consumer may point both at the same database. `__EFMigrationsHistory` is shared per database, so a
consumer running their own migrations beside a shipped set in one database needs the shipped set's
history kept in its own table, or the two histories interleave.

## Per-engine column types

This is the section that answers "why six packages, not three, or one." The three engines share no
type mapping this model uses, so the same `Initial` migration generated against the same base model
produces different column types on each engine. From the ledger's four tables
(`StatesmanHeads`, `StatesmanLeases`, `StatesmanRecords`, `StatesmanSequences`), as generated:

| Property | SQLite | SQL Server | PostgreSQL |
|---|---|---|---|
| `Root` (max 128) | `TEXT` | `nvarchar(128)` | `character varying(128)` |
| `Path` (max 512) | `TEXT` | `nvarchar(512)` | `character varying(512)` |
| `Partition` (max 256) | `TEXT` | `nvarchar(256)` | `character varying(256)` |
| `Revision` | `INTEGER` | `bigint` | `bigint` |
| `GlobalPosition` | `INTEGER` | `bigint` | `bigint` |
| `Operation` (int) | `INTEGER` | `int` | `integer` |
| `Status` (int) | `INTEGER` | `int` | `integer` |
| `ValueType` (max 1024) | `TEXT` | `nvarchar(1024)` | `character varying(1024)` |
| `SchemaVersion` (int) | `INTEGER` | `int` | `integer` |
| `Payload` (nullable) | `BLOB` | `varbinary(max)` | `bytea` |
| `OccurredAt` | `TEXT` | `datetimeoffset` | `timestamp with time zone` |
| `FreshUntil` / `ServeUntil` (nullable) | `TEXT` | `datetimeoffset` | `timestamp with time zone` |
| `Source` (max 256) | `TEXT` | `nvarchar(256)` | `character varying(256)` |
| `CorrelationId` / `CausationId` / `ErrorJson` (nullable) | `TEXT` | `nvarchar(max)` | `text` |
| `MetadataJson` (required, unbounded) | `TEXT` | `nvarchar(max)` | `text` |
| `StatesmanLeases.LeaseId` (max 256, PK) | `TEXT` | `nvarchar(256)` | `character varying(256)` |
| `StatesmanLeases.Token` (max 64) | `TEXT` | `nvarchar(64)` | `character varying(64)` |
| `StatesmanLeases.ExpiresAt` | `TEXT` | `datetimeoffset` | `timestamp with time zone` |
| `StatesmanSequences.Name` (max 128, PK) | `TEXT` | `nvarchar(128)` | `character varying(128)` |
| `StatesmanSequences.Value` | `INTEGER` | `bigint` | `bigint` |

Primary keys are identical in shape across all three engines: `StatesmanHeads` on
`(Root, Path, Partition)`, `StatesmanRecords` on `(Root, Path, Partition, Revision)` plus a unique
index on `GlobalPosition`, `StatesmanLeases` on `LeaseId`, `StatesmanSequences` on `Name`.

The outbox's single `StatesmanOutboxCursors` table, keyed on `OutboxId`:

| Property | SQLite | SQL Server | PostgreSQL |
|---|---|---|---|
| `OutboxId` (max 256, PK) | `TEXT` | `nvarchar(256)` | `character varying(256)` |
| `Position` | `INTEGER` | `bigint` | `bigint` |

## The two engine limitations, restated

Shipping a migration does not change either of these; it only changes where an operator first
encounters them.

**SQL Server's 900-byte clustered-key warning.** `StatesmanRecords`' composite primary key
(`Root`, `Path`, `Partition`, `Revision`) can exceed SQL Server's 900-byte clustered-index key limit at
its declared maximum lengths. `CREATE TABLE` succeeds with a warning either way; an operator who
adopts the shipped SQL Server migration now sees that same warning in migration output, where before
it appeared only in `EnsureCreated` output. Same warning, same reason, unchanged by shipping a
migration — see [the providers page](index.md#outbox-delivery-semantics-and-limitations) for the full
explanation, and the spec's pre-Phase-12 addendum decision 9 for why it is not re-opened.

**PostgreSQL's microsecond `timestamptz` truncation.** PostgreSQL stores `timestamptz` to the
microsecond while a .NET tick is 100 ns, so `OccurredAt`, `FreshUntil`, `ServeUntil`, and the lease
table's `ExpiresAt` lose their last tick digit on that engine. This is a property of the column type
PostgreSQL maps `DateTimeOffset` to, not of how the table was created, and shipping a migration does
not change it — see the spec's pre-Phase-12 addendum decision 12.

Neither limitation is changed by shipping migrations. See spec section "Phase 14 — Entity Framework
Core migrations as code," item 7 ("Pre-Phase-12 addendum decisions 9 and 12 are not re-opened"), for
the full ruling.

## The versioning policy

A shipped migration is a permanent public contract. Five rules govern every future change to any of
the six packages:

- **Append-only.** A shipped migration id is never renamed, removed, or regenerated. A consumer's
  history table holds it, and changing it strands every database that already applied it.
- **Every model change adds a migration to all six packages**, each with a working `Down`. The six
  packages generate different SQL from the same model, so a change landing in five of them is a broken
  sixth — the drift gate (below) fails per package, not once for the whole model.
- **The six ids never align.** A migration id is a UTC timestamp taken at generation, so no two
  packages' ids match even for the same logical change, and no document or code may imply they do.
- **A `ProductVersion` move is a committed diff.** `modelBuilder.HasAnnotation("ProductVersion", …)`
  tracks the Entity Framework Core version at generation time. An Entity Framework Core upgrade that
  changes generated output surfaces as a snapshot diff to review and commit, not to regenerate away.
- **Generated files are committed verbatim.** What `dotnet ef` writes is what ships; nobody hand-edits
  a generated migration, designer, or snapshot file.

Authoring a new migration, once a model change lands in both core packages:

```powershell
dotnet tool restore
dotnet ef migrations add <Name> --project src/Statesman.Persistence.EntityFrameworkCore.Sqlite/Statesman.Persistence.EntityFrameworkCore.Sqlite.csproj --output-dir Migrations
```

repeated for each of the other five packages, substituting its own `--project`. `dotnet tool restore`
reads `.config/dotnet-tools.json`, which pins `dotnet-ef` to the same Entity Framework Core version the
packages reference, so the command is reproducible across machines. Each of the six packages also
references `Microsoft.EntityFrameworkCore.Design` for this purpose, with `PrivateAssets="all"` — that
reference never reaches a consumer; it exists only so `dotnet ef` can run against the project at
authoring time.

## The drift gate

There is no CI job that opens a database connection to check for model drift; the check needs none.
`EntityFrameworkMigrationDriftTests` (for the ledger, in `Statesman.EntityFrameworkCore.Tests`) and
`EntityFrameworkOutboxMigrationDriftTests` (for the outbox, in
`Statesman.Outbox.EntityFrameworkCore.Tests`) each assert, per engine, that the migrations assembly is
non-empty, the model snapshot is not null, and `Database.HasPendingModelChanges()` is `false` — against
an unreachable connection string, because `HasPendingModelChanges()` is a design-time diff against the
snapshot and never contacts a server.

When one of these tests goes red, it means the context's model has moved since the shipped migration
was generated. The failure message says exactly what to do:

> `StatesmanLedgerDbContext's model has drifted from this package's shipped migration. Add a migration
> to ALL SIX packages -- three engines times two contexts -- with `dotnet tool restore && dotnet ef
> migrations add <Name> --project <package> --output-dir Migrations`. Never edit or remove a shipped
> migration: its id is a permanent public contract.`

(The outbox test's failure message names `StatesmanOutboxCursorDbContext` in place of
`StatesmanLedgerDbContext`.) Follow it literally: generate a new migration in all six packages, never
edit or remove the one that already shipped.
