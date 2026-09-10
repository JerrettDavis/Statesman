using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Statesman.TestHelpers;

/// <summary>How many transactions a test needs open against its database at once.</summary>
/// <remarks>
/// This is the one thing the nine Entity Framework Core test files genuinely disagreed about before
/// they shared a fixture, and it is a SQLite constraint rather than a test-design choice:
/// Microsoft.Data.Sqlite cannot host two concurrent transactions on one connection object, so a test
/// that needs two must use a file-backed database rather than the shared open
/// <c>Data Source=:memory:</c> connection the rest use. On a server engine the distinction does not
/// exist and both values create the same thing.
/// </remarks>
public enum EntityFrameworkTestConcurrency
{
    /// <summary>One kept-open in-memory SQLite connection shared by every context.</summary>
    SingleConnection,

    /// <summary>A file-backed SQLite database, so two transactions can be open at once.</summary>
    ConcurrentTransactions,
}

/// <summary>
/// One throwaway database for one test, plus the context factory over it.
/// </summary>
/// <remarks>
/// <para>
/// It lives here rather than in <c>Statesman.Testing</c> on purpose, for the reason
/// <c>PausingTimeProvider.cs</c> already records for the pausing clock: the shipped testing package
/// must not grow a database-provider dependency for a test-only concern. Consuming test projects
/// link this file rather than copying it.
/// </para>
/// <para>
/// Every method is engine-agnostic by signature. This file ships SQLite only; ROADMAP 0.3 Phase 12
/// Task 2 adds SQL Server and PostgreSQL behind the same members, so no call site moves twice.
/// </para>
/// </remarks>
public sealed class EntityFrameworkTestDatabase : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly string? _file;
    private bool _disposed;

    private EntityFrameworkTestDatabase(SqliteConnection? connection, string? file)
    {
        _connection = connection;
        _file = file;
    }

    /// <summary>Creates a throwaway database synchronously.</summary>
    /// <param name="concurrency">How many transactions the test needs open at once.</param>
    public static EntityFrameworkTestDatabase Create(
        EntityFrameworkTestConcurrency concurrency = EntityFrameworkTestConcurrency.SingleConnection)
    {
        if (concurrency == EntityFrameworkTestConcurrency.ConcurrentTransactions)
        {
            return new EntityFrameworkTestDatabase(connection: null, NewSqliteFile());
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new EntityFrameworkTestDatabase(connection, file: null);
    }

    /// <summary>Creates a throwaway database.</summary>
    /// <param name="concurrency">How many transactions the test needs open at once.</param>
    public static async ValueTask<EntityFrameworkTestDatabase> CreateAsync(
        EntityFrameworkTestConcurrency concurrency = EntityFrameworkTestConcurrency.SingleConnection)
    {
        if (concurrency == EntityFrameworkTestConcurrency.ConcurrentTransactions)
        {
            return new EntityFrameworkTestDatabase(connection: null, NewSqliteFile());
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return new EntityFrameworkTestDatabase(connection, file: null);
    }

    /// <summary>Points an options builder at this database.</summary>
    /// <param name="builder">The builder to configure.</param>
    public void Configure(DbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (_connection is not null)
        {
            builder.UseSqlite(_connection);
        }
        else
        {
            builder.UseSqlite($"Data Source={_file}");
        }
    }

    /// <summary>Builds options for one context type against this database.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="configure">Extra configuration applied after the provider is selected.</param>
    public DbContextOptions<TContext> Options<TContext>(
        Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        Configure(builder);
        configure?.Invoke(builder);
        return builder.Options;
    }

    /// <summary>Builds a factory over this database and creates the schema, synchronously.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="create">Constructs one context from the built options.</param>
    /// <param name="configure">Extra configuration applied after the provider is selected.</param>
    public TestDbContextFactory<TContext> CreateFactory<TContext>(
        Func<DbContextOptions<TContext>, TContext> create,
        Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(create);
        var factory = new TestDbContextFactory<TContext>(Options(configure), create);
        using TContext context = factory.CreateDbContext();
        context.Database.EnsureCreated();
        return factory;
    }

    /// <summary>Builds a factory over this database and creates the schema.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="create">Constructs one context from the built options.</param>
    /// <param name="configure">Extra configuration applied after the provider is selected.</param>
    public async ValueTask<TestDbContextFactory<TContext>> CreateFactoryAsync<TContext>(
        Func<DbContextOptions<TContext>, TContext> create,
        Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(create);
        var factory = new TestDbContextFactory<TContext>(Options(configure), create);
        await using TContext context = await factory.CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
        return factory;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();

        // ClearAllPools before the delete, and unconditionally: Microsoft.Data.Sqlite pools
        // file-backed connections, and a pooled handle keeps the file locked on Windows. The
        // file-backed sites deleted the file this way before the seam existed and still must.
        SqliteConnection.ClearAllPools();
        if (_file is not null && File.Exists(_file))
        {
            File.Delete(_file);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // Every teardown step is synchronous, so there is no sync-over-async here and nothing to
        // deadlock. Both interfaces exist because the call sites split between a synchronous
        // IDisposable test class and an async cleanup delegate.
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string NewSqliteFile()
    {
        string file = Path.Combine(
            Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }
}

/// <summary>
/// The one <see cref="IDbContextFactory{TContext}"/> every Entity Framework Core test uses.
/// </summary>
/// <typeparam name="TContext">The context type.</typeparam>
/// <remarks>
/// It takes a construction delegate rather than using reflection because a generic factory cannot
/// call <c>new TContext(options)</c> -- there is no such constraint -- and because reflection would
/// turn a compile error in a test's own context type into a runtime one.
/// </remarks>
public sealed class TestDbContextFactory<TContext> : IDbContextFactory<TContext>
    where TContext : DbContext
{
    private readonly Func<DbContextOptions<TContext>, TContext> _create;

    /// <summary>Creates a factory.</summary>
    /// <param name="options">The options every context is built from.</param>
    /// <param name="create">Constructs one context from those options.</param>
    public TestDbContextFactory(
        DbContextOptions<TContext> options,
        Func<DbContextOptions<TContext>, TContext> create)
    {
        Options = options;
        _create = create;
    }

    /// <summary>The options this factory hands to every context it creates.</summary>
    public DbContextOptions<TContext> Options { get; }

    /// <inheritdoc />
    public TContext CreateDbContext() => _create(Options);

    /// <inheritdoc />
    public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
