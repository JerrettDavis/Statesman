# ROADMAP 0.3 handover

Living status doc for the "durable distributed coordination" effort (`ROADMAP.md` § 0.3). Read
this first when picking the work back up — it points at the canonical artifacts rather than
duplicating them.

## Canonical artifacts

- **Design spec** (architecture, all 8 phases, addenda recording every mid-flight correction):
  `docs/superpowers/specs/2026-09-03-roadmap-0.3-distributed-coordination-design.md`
- **Per-phase implementation plans**: `docs/superpowers/plans/2026-09-03-roadmap-0.3-phase-*.md`
- **Capability matrix** (which provider implements which capability, enforced by a reflection
  test): `docs/architecture/capabilities.md` /
  `tests/Statesman.Capabilities.Tests/CapabilityMatrixTests.cs`
- **Per-phase SDD execution ledgers** (deleted once each phase's final review lands clean —
  absence of `.superpowers/sdd/<plan-name>/` means that phase is fully closed out)

## How this effort works (read once, applies to every phase)

1. Each phase gets a section in the spec doc, then its own implementation plan
   (`writing-plans` skill), then gets executed task-by-task via `subagent-driven-development`:
   fresh implementer subagent per task → per-task reviewer subagent → fix loop if needed → next
   task → final whole-branch reviewer subagent over the whole phase → one more fix wave if needed.
2. **No worktree, no feature branch** — every phase commits directly to `main`. This is a
   deliberate ruling (recorded in every phase's ledger `## Setup` section), matching this repo's
   existing convention (`git log` shows direct-to-main history) and the user's own framing of the
   effort as "iterate until we land green on main."
3. Every commit must pass `python3 eng/validate.py --report artifacts/static-validation.txt`
   (offline structural validator — trailing newlines, no `TODO`/`FIXME`/`NotImplementedException`
   literals, `Statesman.slnx` completeness, etc. — the only pre-existing findings that are OK to
   see are the three gitignored `.remember/*` hygiene warnings) and `dotnet test Statesman.slnx`.
4. After each phase's final review is clean, push to `origin/main` and confirm CI (`gh run list
   --branch main`) — CI, Docs, and CodeQL must all go green, plus (from Phase 1 onward) the
   dedicated `redis-tests` job.
5. When research surfaces that the spec's plan for a phase doesn't actually work as designed
   (this happened twice — Phase 1's lease mechanism placement, Phase 2's decorator-based feed),
   that's a real scope/design decision, not a judgment call to make solo — put it to the user via
   `AskUserQuestion` with real trade-offs, then update the spec doc with an `## Addendum` section
   recording what was found and what was decided, before writing the plan.
6. A capability is a marker interface extending `IStateCapability`
   (`src/Statesman.Abstractions/Ledger.cs`), discovered via `TryGetCapability<T>` — never a
   registry. `IStateCapabilityProvider` (deliberately NOT extending `IStateCapability`) lets a
   wrapping store (`TieredStateLedgerStore`) forward discovery to whichever inner store can back a
   capability. Whether Tiered forwards hot-first or delegates straight to cold is decided
   **per capability** — leases are hot-first (contention happens hot), the change feed is
   cold-only (cold is authoritative) — this is not a single global policy; check the spec's Phase
   1/Phase 2 sections for the reasoning before adding a new capability to Tiered.

## Status by phase

- [x] **Phase 0 — Capability negotiation foundation.** Shipped, on `main`, CI green.
- [x] **Phase 1 — Leases (`IStateLeaseProvider`).** Shipped, on `main`, CI green (including new
  `redis-tests` CI job). Redis + EF Core implement it; FileSystem/InMemory don't (by design).
  **Parked, not fixed** (tracked in the spec's Phase 1 section and addendum, not just the deleted
  ledger): `IStateLease.RenewAsync` has no production caller — the 30s `MaintenanceLeaseTtl` is a
  hard cap with no mid-flight renewal; EF Core's lease acquire has an uncaught
  `DbUpdateConcurrencyException`/PK-violation race on concurrent first-acquisition (degrades
  gracefully — log-and-retry, no crash — but noisy); no shared lease conformance suite across
  providers yet (Redis and EF Core each have their own hand-rolled test suite).
- [~] **Phase 2 — Durable change feed (`IStateChangeFeed`).** All 6 tasks implemented, individually
  reviewed clean (one fix round on Task 5 — FileSystem — for a `GlobalPosition` sort-order bug and
  missing `ImportAsync` test coverage). **The final whole-branch review was dispatched and still
  running when this session ended — check for its result first before doing anything else.** If no
  result is found (the background task didn't survive the session boundary), regenerate the
  package and redispatch:
  ```bash
  bash "$(find ~/.claude -path '*subagent-driven-development/scripts/review-package' 2>/dev/null | head -1)" \
    docs/superpowers/plans/2026-09-03-roadmap-0.3-phase-2-change-feed.md \
    cd7c04b 777eafc5a9648f74709ca281b055cdf28c945524
  ```
  (base = the spec-redesign commit right before this phase's plan was written; head = Task 6's
  commit, the last one in this phase.) Dispatch the final reviewer exactly as described in the
  `subagent-driven-development` skill (`../requesting-code-review/code-reviewer.md` template, most
  capable model). After it comes back: one fix wave if there are findings, one scoped re-review,
  adjudicate residuals (park real-but-non-blocking findings with rulings, same pattern as every
  prior phase), then `dotnet test Statesman.slnx` + `eng/validate.py` clean, push, confirm CI.
  **Already known and parked from Task 4's review** (real, not blocking): Redis's new
  `ChangeFeedKey` sorted set isn't trimmed by `PruneAsync` the way the per-stream history set is —
  unbounded growth over time, needs a retention/pruning follow-up.
  **Design note for whoever plans next**: the spec's Phase 2 section was substantially revised
  mid-effort — the original "generic decorator over `ReadHistoryAsync`" idea doesn't work
  (single-stream vs. cross-stream query shapes are fundamentally different). Every provider now
  implements `IStateChangeFeed` natively. This was a real scope decision put to the user (see the
  spec's "Addendum (pre-Phase-2, 2026-09-03)").
- [ ] **`IStateChangeNotifier` (Redis pub/sub accelerator)** — explicitly deferred to its own small
  follow-on plan, not bundled into Phase 2 (see that plan's "Definition of done"). Genuinely
  optional; not a blocker for anything.
- [ ] **Phase 3 — Partition discovery (`IPartitionCatalog`).** Not started.
- [ ] **Phase 4 — Distributed coherent capture.** Not started. Note: will hit the same
  `DbUpdateConcurrencyException`-handling gap parked in Phase 1's EF Core lease work — resolve that
  properly here rather than parking it a second time.
- [ ] **Phase 5 — Replication lag metadata.** Not started.
- [ ] **Phase 6 — Import/export/restore tooling.** Not started.
- [ ] **Phase 7 — Outbox bridges.** Not started. Depends on the change feed (Phase 2) landing
  first — it does, once Phase 2's final review clears.

## Side task (unrelated to ROADMAP 0.3, done early this session)

NuGet Trusted Publishing wired into `.github/workflows/release.yml` — `NuGet/login@v1` under a
`production` GitHub environment (created this session) with `NUGET_USER=JerrettDavis` set as an
environment variable, replacing the old long-lived `NUGET_API_KEY` secret. Already merged and
pushed; nothing pending here.

## Conventions worth knowing before touching code

- Target `net10.0`, `TreatWarningsAsErrors=true`, xUnit v3 (implicit `using Xunit;` in test
  projects — don't add it explicitly). New public capability interfaces get a `///` doc comment.
- `Assert.SkipUnless(...)` (xUnit v3's dynamic-skip API) gates any test needing live infra (e.g.
  `STATESMAN_TEST_REDIS`) so it skips cleanly rather than fails when that infra isn't present.
- The repo has **zero** `[InternalsVisibleTo]` attributes and the core `Statesman` package has
  **zero** external dependencies beyond `Statesman.Abstractions` — both are deliberate, preserved
  through every phase so far (internal state like `MaintenanceFailures`/`DegradedMaintenanceStores`
  is exposed but intentionally untested directly; no logging framework was pulled into the core
  package for lease/feed degradation tracking).
- Every new provider capability needs: an entry in `docs/architecture/capabilities.md`'s table, a
  matching tuple in `CapabilityMatrixTests.cs`'s `Capabilities`/`Providers` arrays (the completeness
  test catches a missing one automatically), and — if it's genuinely optional — an honest "No" cell
  for providers that can't back it, never a silent degraded implementation.
