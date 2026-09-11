using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

/// <summary>Which database engine the Entity Framework Core suites run against.</summary>
/// <remarks>
/// Selected by environment variable, not by attribute or trait — the same shape every live-Redis
/// test in this repository already uses. When both server variables are set, SQL Server wins, so a
/// PostgreSQL run must clear <c>STATESMAN_TEST_SQLSERVER</c>.
/// </remarks>
public enum EntityFrameworkTestEngine
{
    /// <summary>The default: no server variable set.</summary>
    Sqlite,

    /// <summary>A live SQL Server named by <c>STATESMAN_TEST_SQLSERVER</c>.</summary>
    SqlServer,

    /// <summary>A live PostgreSQL named by <c>STATESMAN_TEST_POSTGRES</c>.</summary>
    PostgreSql,
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
/// Every method is engine-agnostic by signature. Which engine is used is decided by environment
/// variable, once per process — see <see cref="EntityFrameworkTestEngine"/>. Every call site is
/// engine-agnostic; only <see cref="Configure"/> and <see cref="Dispose()"/> branch.
/// </para>
/// </remarks>
public sealed class EntityFrameworkTestDatabase : IDisposable, IAsyncDisposable
{
    private readonly EntityFrameworkTestEngine _engine;
    private readonly SqliteConnection? _connection;
    private readonly string? _file;
    private readonly string? _databaseName;
    private readonly string? _connectionString;
    private bool _disposed;

    private EntityFrameworkTestDatabase(
        EntityFrameworkTestEngine engine,
        SqliteConnection? connection,
        string? file,
        string? databaseName,
        string? connectionString)
    {
        _engine = engine;
        _connection = connection;
        _file = file;
        _databaseName = databaseName;
        _connectionString = connectionString;
    }

    /// <summary>The environment variable naming a live SQL Server instance.</summary>
    public const string SqlServerVariable = "STATESMAN_TEST_SQLSERVER";

    /// <summary>The environment variable naming a live PostgreSQL instance.</summary>
    public const string PostgresVariable = "STATESMAN_TEST_POSTGRES";

    /// <summary>The environment variable that turns the provider's retrying execution strategy on.</summary>
    /// <remarks>
    /// A whole-suite gate rather than a second CI matrix dimension: the two server jobs already have
    /// their engines up, so re-running three projects inside them costs one step each, where a
    /// retry-on/off by three-engine matrix would double two of the slowest jobs in the build for no
    /// additional signal. ROADMAP 0.3 Phase 13, addendum decision 5.
    /// </remarks>
    public const string RetryVariable = "STATESMAN_TEST_EF_RETRY";

    /// <summary>True when <see cref="RetryVariable"/> asks for a retrying execution strategy.</summary>
    public static bool RetryOnFailureRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RetryVariable));

    /// <summary>Which engine every database created in this process runs on.</summary>
    public static EntityFrameworkTestEngine SelectedEngine =>
        !string.IsNullOrWhiteSpace(SqlServerConnectionString) ? EntityFrameworkTestEngine.SqlServer :
        !string.IsNullOrWhiteSpace(PostgresConnectionString) ? EntityFrameworkTestEngine.PostgreSql :
        EntityFrameworkTestEngine.Sqlite;

    /// <summary>True when a live server engine is configured.</summary>
    public static bool ServerEngineConfigured => SelectedEngine != EntityFrameworkTestEngine.Sqlite;

    /// <summary>The skip reason for a test that needs any live server engine.</summary>
    public static string ServerEngineSkipReason =>
        $"Neither {SqlServerVariable} nor {PostgresVariable} is set; skipping tests that require a live server engine.";

    /// <summary>The skip reason for a test that needs a live SQL Server specifically.</summary>
    public static string SqlServerSkipReason =>
        $"{SqlServerVariable} is not set; skipping tests that require a live SQL Server instance.";

    /// <summary>The skip reason for a test that needs a live PostgreSQL specifically.</summary>
    public static string PostgresSkipReason =>
        $"{PostgresVariable} is not the selected engine ({SqlServerVariable} wins when both are set); " +
        "skipping tests that require a live PostgreSQL instance.";

    /// <summary>Which engine this database runs on.</summary>
    public EntityFrameworkTestEngine Engine => _engine;

    /// <summary>
    /// The SQL fragments this engine renders <c>Queryable.Take</c> as, any one of which proves the
    /// limit was pushed to the server.
    /// </summary>
    public string[] TakeSqlFragments => _engine switch
    {
        EntityFrameworkTestEngine.SqlServer => ["TOP(", "FETCH NEXT"],
        _ => ["LIMIT"],
    };

    private static string? SqlServerConnectionString =>
        Environment.GetEnvironmentVariable(SqlServerVariable);

    private static string? PostgresConnectionString =>
        Environment.GetEnvironmentVariable(PostgresVariable);

    /// <summary>Creates a throwaway database synchronously.</summary>
    /// <param name="concurrency">How many transactions the test needs open at once.</param>
    public static EntityFrameworkTestDatabase Create(
        EntityFrameworkTestConcurrency concurrency = EntityFrameworkTestConcurrency.SingleConnection) =>
        SelectedEngine switch
        {
            EntityFrameworkTestEngine.SqlServer => CreateSqlServer(),
            EntityFrameworkTestEngine.PostgreSql => CreatePostgres(),
            _ => CreateSqlite(concurrency, openAsync: false).GetAwaiter().GetResult(),
        };

    /// <summary>Creates a throwaway database.</summary>
    /// <param name="concurrency">How many transactions the test needs open at once.</param>
    public static async ValueTask<EntityFrameworkTestDatabase> CreateAsync(
        EntityFrameworkTestConcurrency concurrency = EntityFrameworkTestConcurrency.SingleConnection) =>
        SelectedEngine switch
        {
            EntityFrameworkTestEngine.SqlServer => CreateSqlServer(),
            EntityFrameworkTestEngine.PostgreSql => CreatePostgres(),
            _ => await CreateSqlite(concurrency, openAsync: true),
        };

    private static async ValueTask<EntityFrameworkTestDatabase> CreateSqlite(
        EntityFrameworkTestConcurrency concurrency,
        bool openAsync)
    {
        if (concurrency == EntityFrameworkTestConcurrency.ConcurrentTransactions)
        {
            return new EntityFrameworkTestDatabase(
                EntityFrameworkTestEngine.Sqlite, connection: null, NewSqliteFile(), databaseName: null, connectionString: null);
        }

        var connection = new SqliteConnection("Data Source=:memory:");
        if (openAsync)
        {
            await connection.OpenAsync();
        }
        else
        {
            connection.Open();
        }

        return new EntityFrameworkTestDatabase(
            EntityFrameworkTestEngine.Sqlite, connection, file: null, databaseName: null, connectionString: null);
    }

    private static EntityFrameworkTestDatabase CreateSqlServer()
    {
        // The name is "statesman_test_" plus a GUID's 32 hex digits: 47 characters, well inside SQL
        // Server's 128 and PostgreSQL's 63, and it cannot carry a quoting escape. DDL cannot be
        // parameterised, so the identifier is quoted as well, belt and braces.
        string name = NewDatabaseName();
        var admin = new SqlConnectionStringBuilder(SqlServerConnectionString!) { InitialCatalog = "master" };
        using (var connection = new SqlConnection(admin.ConnectionString))
        {
            connection.Open();
            Execute(connection, $"CREATE DATABASE [{name}];");

            // ALLOW_SNAPSHOT_ISOLATION must be ON before any connection opens a Snapshot
            // transaction, or BeginTransaction throws at runtime. Phase 12 Task 3's
            // provider-conditional mapping is what needs it; setting it here means every SQL Server
            // database this seam creates is uniform, so no test has to remember.
            Execute(connection, $"ALTER DATABASE [{name}] SET ALLOW_SNAPSHOT_ISOLATION ON;");
        }

        var target = new SqlConnectionStringBuilder(SqlServerConnectionString!) { InitialCatalog = name };
        return new EntityFrameworkTestDatabase(
            EntityFrameworkTestEngine.SqlServer, connection: null, file: null, name, target.ConnectionString);
    }

    private static EntityFrameworkTestDatabase CreatePostgres()
    {
        string name = NewDatabaseName();
        var admin = new NpgsqlConnectionStringBuilder(PostgresConnectionString!) { Database = "postgres" };
        using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            connection.Open();
            Execute(connection, $"CREATE DATABASE \"{name}\";");
        }

        var target = new NpgsqlConnectionStringBuilder(PostgresConnectionString!) { Database = name };
        return new EntityFrameworkTestDatabase(
            EntityFrameworkTestEngine.PostgreSql, connection: null, file: null, name, target.ConnectionString);
    }

    private static string NewDatabaseName() => "statesman_test_" + Guid.NewGuid().ToString("N");

    private static void Execute(DbConnection connection, string sql)
    {
        using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Points an options builder at this database.</summary>
    /// <remarks>
    /// When <see cref="RetryVariable"/> is set, both server arms enable the provider's retrying
    /// execution strategy, so every suite that goes through this seam runs under one. SQLite has no
    /// such strategy and is unaffected.
    /// </remarks>
    /// <param name="builder">The builder to configure.</param>
    public void Configure(DbContextOptionsBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        switch (_engine)
        {
            case EntityFrameworkTestEngine.SqlServer:
                builder.UseSqlServer(_connectionString!, options =>
                {
                    if (RetryOnFailureRequested)
                    {
                        options.EnableRetryOnFailure();
                    }
                });
                break;
            case EntityFrameworkTestEngine.PostgreSql:
                builder.UseNpgsql(_connectionString!, options =>
                {
                    if (RetryOnFailureRequested)
                    {
                        options.EnableRetryOnFailure();
                    }
                });
                break;
            default:
                if (_connection is not null)
                {
                    builder.UseSqlite(_connection);
                }
                else
                {
                    builder.UseSqlite($"Data Source={_file}");
                }

                break;
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

    /// <summary>
    /// Builds options for one context type against this database, with the provider's retrying
    /// execution strategy enabled unconditionally.
    /// </summary>
    /// <remarks>
    /// Deliberately not driven by <c>STATESMAN_TEST_EF_RETRY</c>, which is the whole-suite gate: a
    /// test whose point is observing a retry must not be silently turned into a test of the default
    /// strategy by an unset variable.
    /// </remarks>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="configure">Extra configuration applied after the provider is selected.</param>
    public DbContextOptions<TContext> OptionsWithRetryOnFailure<TContext>(
        Action<DbContextOptionsBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        switch (_engine)
        {
            case EntityFrameworkTestEngine.SqlServer:
                builder.UseSqlServer(_connectionString!, options => options.EnableRetryOnFailure());
                break;
            case EntityFrameworkTestEngine.PostgreSql:
                builder.UseNpgsql(_connectionString!, options => options.EnableRetryOnFailure());
                break;
            default:
                throw new NotSupportedException(
                    "EnableRetryOnFailure is only meaningful against a live server engine.");
        }

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
        switch (_engine)
        {
            case EntityFrameworkTestEngine.SqlServer:
                // ClearAllPools first: DROP DATABASE fails while any session is connected, and a
                // suite that leaks one database per test starts failing on its second run.
                SqlConnection.ClearAllPools();
                DropSqlServer();
                break;
            case EntityFrameworkTestEngine.PostgreSql:
                NpgsqlConnection.ClearAllPools();
                DropPostgres();
                break;
            default:
                _connection?.Dispose();

                // Microsoft.Data.Sqlite pools file-backed connections, and a pooled handle keeps the
                // file locked on Windows. The file-backed sites cleared pools this way before the
                // seam existed and still must.
                SqliteConnection.ClearAllPools();
                if (_file is not null && File.Exists(_file))
                {
                    File.Delete(_file);
                }

                break;
        }
    }

    private void DropSqlServer()
    {
        var admin = new SqlConnectionStringBuilder(SqlServerConnectionString!) { InitialCatalog = "master" };
        using var connection = new SqlConnection(admin.ConnectionString);
        connection.Open();
        Execute(connection, $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;");
        Execute(connection, $"DROP DATABASE [{_databaseName}];");
    }

    private void DropPostgres()
    {
        var admin = new NpgsqlConnectionStringBuilder(PostgresConnectionString!) { Database = "postgres" };
        using var connection = new NpgsqlConnection(admin.ConnectionString);
        connection.Open();
        Execute(connection, $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);");
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
