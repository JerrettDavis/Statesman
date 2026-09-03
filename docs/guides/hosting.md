# Hosting and ASP.NET Core

## Hosted lifecycle

`AddStatesmanHosting()` registers one background service for all roots. At startup it runs each root's warm-up declarations. It then scans active state handles and refreshes partitions whose interval is due.

```csharp
services.AddStatesmanHosting(options =>
{
    options.ScanInterval = TimeSpan.FromSeconds(15);
    options.StopHostOnInitializationFailure = true;
});
```

Only active partition handles participate in interval maintenance. Warm known partitions at startup or touch them through reads, writes, or signals. Statesman does not enumerate an unbounded partition universe from a static declaration.

## Inspection endpoints

```csharp
app.MapStatesman(options =>
{
    options.Prefix = "/_statesman";
    options.AuthorizationPolicy = "operations";
    options.IncludeValuesByDefault = false;
    options.EnableSignals = false;
    options.MaximumHistoryRecords = 500;
});
```

Endpoints include root discovery, manifest, current snapshot, history, and optional signals. Values and signals are conservative by default. A manifest can still reveal type names, paths, and architecture, so production deployments should authorize the entire route group.

Do not place secrets directly in state values merely because endpoint values are disabled. Values may also appear in logs, debugger views, custom observers, or durable providers. Store secret references or encrypted payload types when appropriate.

## Remote client

`StatesmanRemoteClient` reads manifests and sends signals to the optional endpoint surface. It is an operations client, not a transparent distributed `IState<T>` proxy. Remote state federation needs explicit consistency, authorization, and schema contracts and is intentionally not hidden behind the local runtime interface.
