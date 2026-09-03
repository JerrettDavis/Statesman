# Repository release-readiness plan

This repository is considered ready for a public MIT release when the source, tests, packages, automation, and documentation can all be validated from a clean checkout without repository-specific tribal knowledge.

## Success condition

The release bar is:

1. Every committed source project restores, builds, tests, and packs without warnings that indicate an incomplete release path.
2. The documentation set builds as a DocFX site and is deployable to GitHub Pages.
3. CI covers structural validation, cross-platform build/test, coverage reporting, Codecov upload, CodeQL, package creation, and release publishing.
4. Repository-facing assets such as README, contribution files, issue templates, support/security guidance, and samples all describe the same current reality.

## Validation loop

Run the same loop locally and in CI:

1. `python eng/validate.py --report artifacts/static-validation.txt`
2. `dotnet restore Statesman.slnx`
3. `dotnet build Statesman.slnx --configuration Release --no-restore`
4. `dotnet test Statesman.slnx --configuration Release --no-build`
5. Pack the publishable projects under `src/`
6. Build the docs site with `docfx docs/docfx.json`
7. Generate a coverage report from collected Cobertura output and upload it to Codecov

## Closed gaps

### Documentation site

- Added a DocFX configuration and navigation tree for the existing conceptual docs.
- Added a Pages deployment workflow so the docs site can publish from `main`.
- Kept the root project files visible from the site so README, roadmap, contribution, and support material stay discoverable.

### CI and coverage

- Added a dedicated coverage job that produces Cobertura, HTML, badges, and text summary outputs.
- Added Codecov upload wiring so repository coverage is tracked outside raw workflow logs.
- Added a docs-validation job so pull requests catch documentation-site regressions before merge.

### Packaging and release

- Changed local and workflow pack steps to package only publishable source projects under `src/`.
- Preserved checksum generation and optional NuGet publishing when `NUGET_API_KEY` is present.
- Kept package generation aligned with the repository's existing versioning strategy.

### Repository truthfulness

- Removed stale README language that claimed the repository had not been compiled in the authoring environment.
- Documented the release-readiness bar and validation loop explicitly so future changes can preserve it.

## Ongoing guardrails

`eng/validate.py` now treats the DocFX configuration, docs workflow, and coverage workflow expectations as required release surfaces. Regressions in those assets fail fast before a package or release is produced.
