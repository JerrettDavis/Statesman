# Manifest reference

A `StatesmanManifest` contains:

- `id` and declaration `version`
- deterministic `fingerprint`
- root metadata
- containers with path, isolation, direct states, tags, and description
- states with path, value type, schema, partitioning, store, freshness, retention, refresh, source execution, failure mode, fault behavior, write behavior, sources, interactions, tags, and description

Executable delegates are not serialized. Source service/result types and interaction command types are included so a manifest remains useful without embedding implementation code.

Treat the manifest as operational metadata. It may be logged and compared, but production endpoint access should still be authorized.
