namespace Statesman.TestHelpers;

/// <summary>
/// The one place every Redis-backed test project picks a <see cref="RedisKeyLayout"/>, so the whole
/// suite can be re-run against a cluster without editing a test.
/// </summary>
/// <remarks>
/// <para>
/// <c>STATESMAN_TEST_REDIS_KEY_LAYOUT</c> selects the layout. Set it to <c>SingleSlot</c> to run
/// every Redis store in this repository with the per-store hash tag Redis Cluster requires; leave
/// it unset, or set it to anything else, and the suite runs on <see cref="RedisKeyLayout.Legacy"/>,
/// which is what a standalone server gets today. It joins <c>STATESMAN_TEST_REDIS</c>,
/// <c>STATESMAN_TEST_SQLSERVER</c>, <c>STATESMAN_TEST_POSTGRES</c> and
/// <c>STATESMAN_TEST_EF_RETRY</c>; <c>CONTRIBUTING.md</c> lists all five.
/// </para>
/// <para>
/// The variable selects a layout and never a server. A cluster still needs
/// <c>STATESMAN_TEST_REDIS</c> pointed at it, and setting this variable alone against a standalone
/// server is a supported configuration that simply exercises the tagged key shape.
/// </para>
/// <para>
/// <c>Statesman.Outbox.Redis</c> deliberately has no equivalent: every one of its operations is
/// single-key, so it passes against a cluster unchanged and has no layout to choose.
/// </para>
/// </remarks>
internal static class RedisTestLayout
{
    /// <summary>The environment variable that selects the layout.</summary>
    public const string LayoutVariable = "STATESMAN_TEST_REDIS_KEY_LAYOUT";

    /// <summary>The layout every store in this run uses.</summary>
    public static RedisKeyLayout Layout =>
        string.Equals(
            Environment.GetEnvironmentVariable(LayoutVariable),
            nameof(RedisKeyLayout.SingleSlot),
            StringComparison.OrdinalIgnoreCase)
            ? RedisKeyLayout.SingleSlot
            : RedisKeyLayout.Legacy;

    /// <summary>Store options carrying the selected layout.</summary>
    /// <param name="ownsConnection">Whether the store disposes the multiplexer it is handed.</param>
    /// <returns>Options that are otherwise entirely default.</returns>
    public static RedisStateLedgerStoreOptions Options(bool ownsConnection = false) => new()
    {
        KeyLayout = Layout,
        OwnsConnection = ownsConnection,
    };

    /// <summary>
    /// The key prefix a store of this name writes under the selected layout, the test-side mirror
    /// of <c>RedisStateLedgerStore.KeyScope</c>. A test that asserts on a key by hand builds it from
    /// here rather than from a literal, so it stays true under both layouts.
    /// </summary>
    /// <param name="storeName">The store's name, as passed to its constructor.</param>
    /// <returns>Everything before the first key suffix, with no trailing colon.</returns>
    public static string Scope(string storeName) => Layout == RedisKeyLayout.SingleSlot
        ? $"{{statesman:{storeName}}}"
        : $"statesman:{storeName}";
}
