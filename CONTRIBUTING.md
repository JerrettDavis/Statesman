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

## Change expectations

Runtime changes need focused unit tests and should add an end-to-end test when behavior crosses the host, transport, or provider boundary. Provider changes should prove conditional append, ordering, import, history, retention, cancellation, and failure behavior. Analyzer changes need positive, negative, boundary, and false-positive tests. Public declaration changes must update the manifest reference and demonstrate deterministic output.

Reducers and requirements must remain deterministic with respect to their inputs. A reducer may be re-evaluated after an optimistic conflict, so it must not send messages, write files, charge a card, or perform another non-idempotent side effect.

## Design process

Substantial capabilities should include an ADR under `docs/adr`. Prefer extending an optional capability interface over widening `IStateLedgerStore` with behavior that not every provider can honor. Do not describe process-local behavior with distributed language.

## Compatibility

Avoid source-breaking changes unless the same result cannot be achieved with an additive API. Changes to manifest canonicalization require an explicit compatibility decision because they alter fingerprints. Stored record changes require an upgrade and rollback story.

## Pull requests

Keep a pull request centered on one guarantee. Include the declaration shape, runtime behavior, provider implications, tests, documentation, and migration notes together. The pull request template captures the minimum review surface.
