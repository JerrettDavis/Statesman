namespace Statesman.Conformance.Tests;

/// <summary>
/// The shared distributed-capture suite, run against the tiered provider over an Entity Framework
/// Core cold authority (<see cref="ConformanceProviders.TieredOverEntityFrameworkAsync"/>), not the
/// in-memory cold tier every other tiered subclass in this project uses. <c>CaptureAsync</c> always
/// delegates to cold, and the in-memory provider has no <see cref="IDistributedCapture"/>, so tiered
/// over in-memory throws <see cref="NotSupportedException"/> naming the cold tier instead of
/// exercising the delegation this suite is meant to observe. Entity Framework Core rather than Redis
/// so this subclass runs with no live infrastructure. ROADMAP 0.3 addendum decision 65.
/// </summary>
public sealed class TieredDistributedCaptureConformanceTests : DistributedCaptureConformanceTests
{
    protected override ValueTask<ConformanceStore?> CreateAsync() =>
        ConformanceProviders.TieredOverEntityFrameworkAsync(TimeProvider.System);
}
