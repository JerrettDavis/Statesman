using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Statesman;

public sealed class StatesmanEndpointOptions
{
    public string Prefix { get; set; } = "/_statesman";

    public bool IncludeValuesByDefault { get; set; }

    public bool EnableSignals { get; set; }

    public string? AuthorizationPolicy { get; set; }

    public int MaximumHistoryRecords { get; set; } = 500;
}

public static class StatesmanEndpointRouteBuilderExtensions
{
    public static RouteGroupBuilder MapStatesman(
        this IEndpointRouteBuilder endpoints,
        Action<StatesmanEndpointOptions>? configure = null)
    {
        var options = new StatesmanEndpointOptions();
        configure?.Invoke(options);
        if (options.MaximumHistoryRecords <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "Maximum history records must be greater than zero.");
        }

        RouteGroupBuilder group = endpoints.MapGroup(options.Prefix);
        if (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            group.RequireAuthorization(options.AuthorizationPolicy);
        }

        group.MapGet("/", (IStatesmanRegistry registry) =>
            Results.Ok(registry.All.Select(root => root.Manifest).OrderBy(manifest => manifest.Id)));

        group.MapGet("/{root}/manifest", (string root, IStatesmanRegistry registry) =>
        {
            if (!registry.TryGet(root, out IStatesman? resolvedStatesman) || resolvedStatesman is null)
            {
                return Results.NotFound();
            }

            IStatesman statesman = resolvedStatesman;
            return Results.Ok(statesman.Manifest);
        });

        group.MapGet("/{root}/state/{**path}", async (
            string root,
            string path,
            string? partition,
            bool? includeValue,
            IStatesmanRegistry registry,
            CancellationToken cancellationToken) =>
        {
            if (!registry.TryGet(root, out IStatesman? resolvedStatesman) || resolvedStatesman is null)
            {
                return Results.NotFound();
            }

            IStatesman statesman = resolvedStatesman;
            try
            {
                IStateSnapshot snapshot = await statesman.GetAsync(
                    new StateReference(new StatePath(path), Partition(partition)),
                    StateReadOptions.Current,
                    cancellationToken).ConfigureAwait(false);
                return Results.Ok(StateSnapshotEnvelope.From(
                    snapshot,
                    includeValue ?? options.IncludeValuesByDefault));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapGet("/{root}/history/{**path}", async (
            string root,
            string path,
            string? partition,
            int? take,
            bool? includeValue,
            IStatesmanRegistry registry,
            CancellationToken cancellationToken) =>
        {
            if (!registry.TryGet(root, out IStatesman? resolvedStatesman) || resolvedStatesman is null)
            {
                return Results.NotFound();
            }

            IStatesman statesman = resolvedStatesman;
            int count = Math.Clamp(take ?? 100, 1, options.MaximumHistoryRecords);
            var history = new List<StateSnapshotEnvelope>();
            try
            {
                await foreach (IStateSnapshot snapshot in statesman.HistoryAsync(
                    new StateReference(new StatePath(path), Partition(partition)),
                    new StateHistoryOptions { Take = count },
                    cancellationToken).ConfigureAwait(false))
                {
                    history.Add(StateSnapshotEnvelope.From(
                        snapshot,
                        includeValue ?? options.IncludeValuesByDefault));
                }
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }

            return Results.Ok(history);
        });

        if (options.EnableSignals)
        {
            group.MapPost("/{root}/signals/{signal}", async (
                string root,
                string signal,
                StateSignalRequest? request,
                IStatesmanRegistry registry,
                CancellationToken cancellationToken) =>
            {
                if (!registry.TryGet(root, out IStatesman? resolvedStatesman) || resolvedStatesman is null)
                {
                    return Results.NotFound();
                }

                IStatesman statesman = resolvedStatesman;
                StatePartition? signalPartition = string.IsNullOrWhiteSpace(request?.Partition)
                    ? default(StatePartition?)
                    : new StatePartition(request.Partition!);
                await statesman.SignalAsync(
                    new StateSignal(
                        signal,
                        signalPartition,
                        request?.Metadata),
                    cancellationToken).ConfigureAwait(false);
                return Results.Accepted();
            });
        }

        return group;
    }

    private static StatePartition Partition(string? value) =>
        string.IsNullOrWhiteSpace(value) ? StatePartition.Default : new StatePartition(value);

    public sealed record StateSignalRequest(
        string? Partition = null,
        IReadOnlyDictionary<string, string>? Metadata = null);

    public sealed record StateSnapshotEnvelope(
        string Root,
        string Path,
        string Partition,
        string ValueType,
        long Revision,
        long GlobalPosition,
        DateTimeOffset ObservedAt,
        DateTimeOffset? OccurredAt,
        StateOperation? Operation,
        StateStatus Status,
        bool HasValue,
        bool IsFresh,
        bool IsStale,
        DateTimeOffset? FreshUntil,
        DateTimeOffset? ServeUntil,
        object? Value,
        StateError? Error,
        IReadOnlyDictionary<string, string> Metadata)
    {
        public static StateSnapshotEnvelope From(IStateSnapshot snapshot, bool includeValue) => new(
            snapshot.Address.Root,
            snapshot.Address.Path.Value,
            snapshot.Address.Partition.Value,
            snapshot.ValueType.FullName ?? snapshot.ValueType.Name,
            snapshot.Revision,
            snapshot.GlobalPosition,
            snapshot.ObservedAt,
            snapshot.OccurredAt,
            snapshot.Operation,
            snapshot.Status,
            snapshot.HasValue,
            snapshot.IsFresh,
            snapshot.IsStale,
            snapshot.FreshUntil,
            snapshot.ServeUntil,
            includeValue ? snapshot.UntypedValue : null,
            snapshot.Error,
            snapshot.Metadata);
    }
}
