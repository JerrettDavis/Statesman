# ADR 0007: Roots and containers express different boundary strengths

## Status

Accepted.

## Decision

Attached and isolated containers remain path-scoped views inside one Statesman root. Separate roots have independent declarations, identities, lifecycles, active handles, and observation streams. Storage failure isolation requires separate provider infrastructure and is not implied merely by registering another root.

## Consequences

The API does not overstate container isolation. Applications can share infrastructure deliberately while preserving authority identity, or use different stores and credentials when operational disconnection is required.
