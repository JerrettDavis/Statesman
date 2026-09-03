# Glossary

**Authority**: the component through which accepted state changes are coordinated and observed. Authority does not imply that Statesman physically owns every source system.

**Container**: a path-scoped group of declarations inside a root. An isolated container strengthens scoped access intent but does not create separate storage by itself.

**Definition**: the frozen declaration of one typed state path.

**Fingerprint**: the SHA-256 digest of the normalized manifest with an empty fingerprint field.

**Global position**: a provider-monotonic ordering value across records accepted by one provider authority.

**Interaction**: a named typed command, requirements, and reducer declared for a state.

**Ledger**: append-only state lineage plus retention. It is not an arbitrary query model.

**Partition**: one independently revisioned instance of a state definition.

**Record**: the provider representation of one accepted operation.

**Revision**: a strictly increasing sequence number within one state address.

**Root**: an independently declared and registered state authority with its own identity, manifest, lifecycle, handles, and observation stream.

**Snapshot**: an immutable observation of a record at a particular time, including dynamically evaluated freshness.

**Source**: a proactive acquisition function that produces a complete state or a facet to project into it.

**State address**: root, path, and partition together.

**Tombstone**: a `Cleared` record that deliberately represents absence.
