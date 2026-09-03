# Changelog

All notable changes to Statesman are documented here. The project follows Semantic Versioning once the public API reaches 1.0. Preview releases may refine APIs when doing so materially improves the authority or consistency model.

## [Unreleased]

### Added

- declaration-driven state runtime and deterministic manifest fingerprint
- typed keys, partitions, attached and isolated containers, and multiple registered roots
- composed proactive loaders with sequential or parallel fetch and require-all or best-effort failure policies
- reactive writes, typed interactions, requirements, reducers, invariants, and schema migrations
- immutable snapshots, explicit read modes, freshness windows, stale-while-revalidate, and last-known-value fault behavior
- append-only ledger operations, optimistic revisions, retention, history, local capture, and observation
- in-memory, filesystem, Redis, Entity Framework Core, and tiered hot/cold providers
- dependency injection, hosting, HTTP, ASP.NET Core, analyzer, and testing packages
- fluent `StateSeedBuilder` scenario setup and portable `StateFixture` capture/load/apply workflows
- untyped runtime mutation operations for dynamic test/tooling orchestration without bypassing state rules
- console, ASP.NET Core, migration, multi-root, and repeatable testing/fixture samples
- unit, provider, analyzer, dependency-injection, and end-to-end test projects
- cross-platform CI, CodeQL, package, release, and dependency-update workflows

### Fixed

- assign `net10.0` explicitly to `Statesman.Sample.Testing`, which previously had no effective target framework
- chain `tests/Directory.Build.props` to the repository build props so test projects inherit implicit usings, nullable analysis, language settings, and warning policy
- make Nerdbank.GitVersioning conditional on Git metadata so downloaded source archives can restore and build outside a Git checkout
- evaluate package README inclusion from `Directory.Build.targets`, after each project has declared `IsPackable`
- harden analyzer nullable flow and analyzer-test metadata reference construction
- replace invalid nullable `TimeSpan` relational patterns with explicit value comparisons
- extend offline validation to catch missing target frameworks, nested `Directory.Build.props` shadowing, and missing solution configuration mappings

### Notes

This archive was assembled in an environment without a .NET SDK. The offline structural validator is included and its report is packaged, but compilation and execution must be performed by the included build scripts or CI.
