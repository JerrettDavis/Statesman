# Contributing to Statesman

Statesman changes should begin with the guarantee they introduce or preserve. A fluent API is not sufficient justification by itself. Describe the authority boundary, process boundary, ordering, durability, consistency, failure behavior, and migration path before changing the public contract.

## Local validation

The repository pins its SDK in `global.json`.

```bash
./eng/build.sh
```

On Windows:

```powershell
./eng/build.ps1
```

The build validates repository structure, restores packages, compiles every project, runs the complete test suite with coverage, and creates NuGet packages under `artifacts/packages`.

### Test environment variables

Every suite runs offline by default and gates anything needing a live server behind an honest skip.
Five variables opt in:

| Variable | Effect |
|---|---|
| `STATESMAN_TEST_REDIS` | A Redis connection string, for example `localhost:6379`. Unset, every Redis-backed test skips. |
| `STATESMAN_TEST_REDIS_KEY_LAYOUT` | `SingleSlot` runs every Redis ledger store with the per-store hash tag Redis Cluster requires. Unset, the suite uses `Legacy`, the default key shape. It selects a layout, never a server: a cluster still needs `STATESMAN_TEST_REDIS` pointed at it. |
| `STATESMAN_TEST_SQLSERVER` | A SQL Server connection string. Unset, the SQL Server suites skip. |
| `STATESMAN_TEST_POSTGRES` | A PostgreSQL connection string. Unset, the PostgreSQL suites skip. |
| `STATESMAN_TEST_EF_RETRY` | `1` re-runs the Entity Framework Core suites under the provider's retrying execution strategy. |

CI sets the first in `redis-tests`, the first two in `redis-cluster`, and the rest in
`sqlserver-tests` and `postgres-tests`.

## Change expectations

Runtime changes need focused unit tests and should add an end-to-end test when behavior crosses the host, transport, or provider boundary. Provider changes should prove conditional append, ordering, import, history, retention, cancellation, and failure behavior. Analyzer changes need positive, negative, boundary, and false-positive tests. Public declaration changes must update the manifest reference and demonstrate deterministic output.

Reducers and requirements must remain deterministic with respect to their inputs. A reducer may be re-evaluated after an optimistic conflict, so it must not send messages, write files, charge a card, or perform another non-idempotent side effect.

## Design process

Substantial capabilities should include an ADR under `docs/adr`. Prefer extending an optional capability interface over widening `IStateLedgerStore` with behavior that not every provider can honor. Do not describe process-local behavior with distributed language.

## Compatibility

Avoid source-breaking changes unless the same result cannot be achieved with an additive API. Changes to manifest canonicalization require an explicit compatibility decision because they alter fingerprints. Stored record changes require an upgrade and rollback story.

## Pull requests

Keep a pull request centered on one guarantee. Include the declaration shape, runtime behavior, provider implications, tests, documentation, and migration notes together. The pull request template captures the minimum review surface.

## Releasing

Package versions come from `version.json` through Nerdbank.GitVersioning, not from the Git tag. A release is cut by pushing a `vMAJOR.MINOR.PATCH` tag, and the release workflow refuses to build if the tag does not name the version `version.json` yields (`eng/check-release-tag.sh` runs the same check locally). To release:

1. Set `"version"` in `version.json` to the release number (for example `"0.3.0"`, or `"0.4.0-preview.1"` for a prerelease), move the `[Unreleased]` changelog entries under a matching heading, and merge that to `main`.
2. Tag that commit `v<version>` and push the tag. The `Release` workflow builds, tests, packs every project under `src`, publishes to NuGet through trusted publishing, and creates the GitHub release with checksums.
3. Bump `version.json` on `main` to the next planned version so continuous builds stop carrying the released number.

Re-running the workflow for an existing tag is safe: NuGet pushes skip duplicates and the GitHub release's assets are replaced.
