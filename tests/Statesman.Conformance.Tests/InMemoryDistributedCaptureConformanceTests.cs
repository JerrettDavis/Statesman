namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared distributed-capture suite, run against the in-memory provider. It has no real
/// cross-process guarantee to offer, so per <c>docs/architecture/capabilities.md</c> it does not
/// implement <see cref="IDistributedCapture"/>, and every fact here skips with an honest "No".
/// </summary>
public sealed class InMemoryDistributedCaptureConformanceTests : DistributedCaptureConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.InMemoryAsync(TimeProvider.System);
}
