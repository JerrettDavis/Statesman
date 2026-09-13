# Public API compatibility

Statesman gates every package's public surface against what it already shipped, so a breaking
change is caught at `dotnet pack` time instead of being discovered by a consumer after release.

## What the gate is

The root `Directory.Build.props` sets `EnablePackageValidation` (validates a package against itself —
for example, that a member removed from one target framework is removed from all of them);
`src/Directory.Build.props` sets `PackageValidationBaselineVersion` = `0.3.0` (validates the package
being built against the last version consumers already have). The gate runs on `dotnet pack`, which both `.github/workflows/ci.yml`'s
`pack` job and `.github/workflows/release.yml` already invoke after `dotnet restore Statesman.slnx` —
restore is what downloads the baseline nupkg, so no workflow needed editing to pick up the gate.

## Which packages have a baseline

`v0.3.0` published fifteen of this repository's twenty-two packages. Those fifteen inherit
`PackageValidationBaselineVersion` = `0.3.0` from `src/Directory.Build.props` and are validated
against it on every pack.

The other seven were not part of `v0.3.0` and have no baseline to validate against, so each blanks
the property in its own `.csproj`:

- `Statesman.Outbox.EntityFrameworkCore`
- `Statesman.Persistence.EntityFrameworkCore.Sqlite`
- `Statesman.Persistence.EntityFrameworkCore.SqlServer`
- `Statesman.Persistence.EntityFrameworkCore.PostgreSQL`
- `Statesman.Outbox.EntityFrameworkCore.Sqlite`
- `Statesman.Outbox.EntityFrameworkCore.SqlServer`
- `Statesman.Outbox.EntityFrameworkCore.PostgreSQL`

Blanking the property disables baseline validation for that project alone; `EnablePackageValidation`'s
other checks still run. Each of these packages' first published version becomes its own baseline
from `0.4.0` onward — see "What happens at 0.4.0" below.

## Why the explanations live here, not in the suppression files

Each baselined package that carries a deliberate break has a `CompatibilitySuppressions.xml` next
to its `.csproj`. That file is **generated output**: running `dotnet pack` with
`-p:ApiCompatGenerateSuppressionFile=true` rewrites it whole. A hand-added XML comment inside it
does not survive that regeneration — measured directly during this gate's introduction. So every
suppression is explained here instead, one row per entry, and never inside the XML.

## Suppressions

### `IStateChangeFeed.ReadAsync` gains a parameter

`IStateChangeFeed.ReadAsync` now takes a `StateChangeReadOptions` between the cursor and the
cancellation token — see the `### Breaking` entry in [`CHANGELOG.md`](../../CHANGELOG.md) for the
full rationale (a bounded, provider-native `Take` on every change feed). The interface change and
its five implementers account for 19 of the 22 suppressions this gate carries, spread across six
packages — the five packages that implement `ReadAsync` plus `Statesman.Abstractions`, which holds
the interface declaration itself:

| Package | Diagnostic(s) | Member |
|---|---|---|
| `Statesman.Abstractions` | `CP0002` × 3, `CP0006` × 3 | `IStateChangeFeed.ReadAsync` (the old three-parameter overload is now `CP0002`-missing; the new four-parameter member is `CP0006`-added), once per target framework |
| `Statesman` | `CP0002` × 3 | `InMemoryStateLedgerStore.ReadAsync`, once per target framework |
| `Statesman.Persistence.FileSystem` | `CP0002` × 3 | `FileSystemStateLedgerStore.ReadAsync`, once per target framework |
| `Statesman.Persistence.Redis` | `CP0002` × 3 | `RedisStateLedgerStore.ReadAsync`, once per target framework |
| `Statesman.Persistence.Tiered` | `CP0002` × 3 | `TieredStateLedgerStore.ReadAsync`, once per target framework |
| `Statesman.Persistence.EntityFrameworkCore` | `CP0002` × 1 | `EntityFrameworkStateLedgerStore<TContext>.ReadAsync`, `net10.0` only (the package targets `net10.0` alone) |

### `OutboxCursorFile` becomes `internal`

`Statesman.Outbox.OutboxCursorFile` — the on-disk shape of a filesystem outbox cursor file — shipped
`public` in `v0.3.0` with no consumer anywhere in this repository. A serialization detail on the
public surface is a compatibility obligation nobody asked for, so the type was made `internal`
deliberately in the `0.4.0-alpha` window, the same window that carries the `IStateChangeFeed.ReadAsync`
break above (ROADMAP 0.3 Phase 15 addendum decision 43) — see the "`Statesman.Outbox.OutboxCursorFile`
is now `internal`" entry under `### Breaking` in [`CHANGELOG.md`](../../CHANGELOG.md) for the full
rationale. This accounts for the remaining 3
suppressions:

| Package | Diagnostic | Member |
|---|---|---|
| `Statesman.Outbox` | `CP0001` × 3 | `Statesman.Outbox.OutboxCursorFile`, once per target framework |

ROADMAP 0.3 Phase 16 (the shared conformance suite, the rate-limited diagnostics surface, and the
health check) is additive only — it added no `CompatibilitySuppressions.xml` entry, so the seven
files above are still seven.

## What a contributor does when the gate fires

1. **Decide whether the break is intended.** Most of the time it is not — fix the code so the
   public surface matches what already shipped.
2. **If the break is intended**, regenerate that project's suppression file:
   ```bash
   dotnet restore Statesman.slnx
   dotnet build Statesman.slnx --configuration Release -m:1 -p:UseSharedCompilation=false
   dotnet pack src/<Project>/<Project>.csproj --configuration Release --no-build \
     -p:ApiCompatGenerateSuppressionFile=true --output artifacts/packages
   ```
   **Regeneration rewrites the file whole** — it is not additive. Before committing, diff the new
   file against the old one and confirm every pre-existing entry is still present; a regeneration
   that silently drops an old suppression means that old break is no longer covered and will need
   re-litigating, which is the failure mode this page exists to catch.
3. Add a `### Breaking` entry to [`CHANGELOG.md`](../../CHANGELOG.md) describing the break and the
   reason it was taken.
4. Add a row to this page (a new subsection under "Suppressions", following the shape above).

## What happens at `0.4.0`

When `0.4.0` ships:

- Bump `PackageValidationBaselineVersion` once in `src/Directory.Build.props`.
- Delete the seven per-project overrides listed above — every package will then have shipped at
  `0.4.0` or later, so the inherited baseline is correct for all twenty-two.
- Delete every suppression whose break is now below the new baseline — a break already present in
  the version being validated against needs no suppression.
