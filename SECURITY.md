# Security policy

## Reporting a vulnerability

Report suspected vulnerabilities privately through the repository's GitHub Security Advisory form. Do not open a public issue with credentials, state payloads, connection strings, filesystem locations, authorization policies, or exploit details.

## Supported versions

Until 1.0, only the latest preview release receives security fixes. A supported-version matrix will be published when stable releases begin.

## Security boundaries

Statesman does not provide an identity system. Authorization belongs at application services, interaction requirements, inspection endpoints, and provider credentials. The ASP.NET Core package keeps values hidden and signals disabled by default, but applications must still attach an authorization policy before exposing manifests, histories, or state values outside a trusted network.

Payload encryption is supplied through `IStateSerializer` or provider infrastructure. Redis, database, filesystem, and transport encryption remain deployment responsibilities. State metadata should be treated as potentially sensitive telemetry and must not be used as an authorization decision merely because it carries an actor or correlation id.

Consumers should assume state values and historical revisions may contain personal or regulated data. Retention, backup, export, and deletion policies must be selected accordingly.
