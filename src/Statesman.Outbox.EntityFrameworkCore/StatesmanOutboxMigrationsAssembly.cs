using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Statesman.Outbox.EntityFrameworkCore;

// Derived from an Entity Framework Core INTERNAL type, deliberately, and this is the only place in
// the package that does it.
//
// Entity Framework Core discovers migrations and model snapshots by comparing
// DbContextAttribute.ContextType to the running context's type with reference equality, never with
// IsAssignableFrom. Both shipped contexts are designed to be subclassed -- that is what every test
// and every documentation page in this repository does -- so a migration generated against the base
// context is INVISIBLE to the subclass that consumes it, and Database.Migrate() then returns
// normally having created only the history table. No exception. No log the consumer reads. The
// failure surfaces at the first query, as a missing table.
//
// There is no public API for the filter, no public IMigrationsAssembly base to start from, and
// reimplementing the interface outright would mean reimplementing CreateMigration and FindMigrationId
// as well. Deriving and overriding the two discovery members is the smallest change that fixes it.
//
// If a future Entity Framework Core release changes this type, the build breaks loudly at EF1001 or
// at the override signatures, and the two seam tests in EntityFrameworkMigrationsSeamTests go red.
// It is roughly forty lines and is re-derivable from the failure. The suppression stays local to the
// lines that earn it and is never moved into a project-level NoWarn, which would silently cover an
// unrelated internal-API use added later. ROADMAP 0.3 Phase 14, addendum decision 22.
//
// Duplicated from Statesman.Persistence.EntityFrameworkCore rather than shared. The two core packages
// share no assembly and neither may reference the other -- that separation is the whole reason
// StatesmanOutboxCursorDbContext exists in a package of its own -- so a third shared package that both
// would have to take is a worse trade than sixty duplicated lines. Addendum decision 22.
#pragma warning disable EF1001

/// <summary>
/// An <see cref="IMigrationsAssembly"/> that accepts a migration declared for a base context when the
/// running context derives from it.
/// </summary>
/// <remarks>
/// Registered by <see cref="StatesmanOutboxMigrations.UseStatesmanOutboxMigrations"/>; consumers never
/// name this type. Without it, a shipped migration applied to a consumer's subclass of
/// <see cref="StatesmanOutboxCursorDbContext"/> is a silent no-op.
/// </remarks>
public sealed class StatesmanOutboxMigrationsAssembly
    : Microsoft.EntityFrameworkCore.Migrations.Internal.MigrationsAssembly
{
    private readonly Type _contextType;

    // Entity Framework Core's MigrationsAssembly keeps its logger in a private field and exposes
    // only Migrations, ModelSnapshot and Assembly (measured), so this type needs its own.
    private readonly IDiagnosticsLogger<DbLoggerCategory.Migrations> _logger;

    private IReadOnlyDictionary<string, TypeInfo>? _migrations;
    private ModelSnapshot? _modelSnapshot;
    private bool _modelSnapshotResolved;

    /// <summary>Creates the assembly. Entity Framework Core resolves this constructor.</summary>
    /// <param name="currentContext">The context being migrated.</param>
    /// <param name="options">The context's options, which name the migrations assembly.</param>
    /// <param name="idGenerator">Entity Framework Core's migration-id generator.</param>
    /// <param name="logger">Entity Framework Core's migrations logger.</param>
    public StatesmanOutboxMigrationsAssembly(
        ICurrentDbContext currentContext,
        IDbContextOptions options,
        IMigrationsIdGenerator idGenerator,
        IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
        : base(currentContext, options, idGenerator, logger)
    {
        // No null guard on currentContext. The base constructor above already dereferences
        // currentContext.Context.GetType(), so a null argument throws a raw NullReferenceException
        // from there before any statement in this body runs -- the guard that used to stand here was
        // unreachable, and no test can pin behaviour that cannot happen. ROADMAP 0.3 Phase 14
        // deferred Minor (a); Phase 15 addendum decision 45.
        _logger = logger;
        _contextType = currentContext.Context.GetType();
    }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, TypeInfo> Migrations
    {
        get
        {
            if (_migrations is not null)
            {
                return _migrations;
            }

            // SortedList with an ordinal comparer, because migration ids are UTC timestamps and
            // Entity Framework Core applies them in key order. Ordinal, not culture-aware: an id is
            // an invariant identifier, not text for a human.
            var found = new SortedList<string, TypeInfo>(StringComparer.Ordinal);
            foreach (TypeInfo candidate in ConstructibleTypes())
            {
                // The two cheap structural filters run FIRST, and the reorder is load-bearing rather
                // than cosmetic: the id check used to short-circuit ahead of them, so moving the
                // diagnostic below without reordering would log once for every type in the consumer's
                // assembly that is not a migration at all.
                if (!typeof(Migration).IsAssignableFrom(candidate.AsType()) || !DeclaredForThisContext(candidate))
                {
                    continue;
                }

                string? id = candidate.GetCustomAttribute<MigrationAttribute>()?.Id;
                if (id is null)
                {
                    // A type that targets THIS context and derives from Migration but carries no
                    // [Migration] attribute is a migration its author meant to ship, so skipping it
                    // in silence is the same class of failure this whole seam exists to remove.
                    // Entity Framework Core's own MigrationsAssembly logs this event, and both
                    // RelationalLoggerExtensions and this overload are public API taking exactly the
                    // IDiagnosticsLogger<DbLoggerCategory.Migrations> this type is constructed with
                    // -- measured: no EF1001, with a control proving the analyzer active.
                    //
                    // NOT the situation in LoadableDefinedTypes below. That path degrades silently
                    // because its only public logging extension, CoreLoggerExtensions
                    // .TypeLoadingErrorWarning, is keyed to IDiagnosticsLogger<DbLoggerCategory.Model>
                    // -- a DIFFERENT category, which is the whole reason it cannot be logged from
                    // here. The two are not analogous. Addendum decisions 36 and 44.
                    _logger.MigrationAttributeMissingWarning(candidate);
                    continue;
                }

                found.Add(id, candidate);
            }

            return _migrations = found;
        }
    }

    /// <inheritdoc />
    public override ModelSnapshot? ModelSnapshot
    {
        get
        {
            // Caches both outcomes, the way Migrations above does: a miss re-scans
            // ConstructibleTypes() on every access otherwise. Cosmetic under normal use -- the miss
            // path only happens on a misconfiguration that is about to throw anyway -- but cheap and
            // consistent to fix. Final review, Minor 5.
            if (_modelSnapshotResolved)
            {
                return _modelSnapshot;
            }

            foreach (TypeInfo candidate in ConstructibleTypes())
            {
                if (!typeof(ModelSnapshot).IsAssignableFrom(candidate.AsType())
                    || !DeclaredForThisContext(candidate))
                {
                    continue;
                }

                _modelSnapshot = (ModelSnapshot)Activator.CreateInstance(candidate.AsType())!;
                break;
            }

            _modelSnapshotResolved = true;
            return _modelSnapshot;
        }
    }

    // The one line this whole type exists for: IsAssignableFrom where Entity Framework Core uses
    // reference equality. A subclass of the declared context is accepted; an unrelated context is
    // not, so a database holding two contexts' migrations in one assembly still keeps them apart.
    // Pinned by An_unrelated_context_discovers_no_migration_from_this_assembly, whose break lever is
    // replacing the IsAssignableFrom call with `true`.
    private bool DeclaredForThisContext(TypeInfo candidate)
    {
        Type? declared = DeclaredContextType(candidate.AsType());
        return declared is not null && declared.IsAssignableFrom(_contextType);
    }

    // One level at a time with inherit: false, mirroring Entity Framework Core's own
    // GetDbContextType. DbContextAttribute is Inherited=True AND AllowMultiple=True (measured), so
    // the single-attribute GetCustomAttribute<T>() overload this replaces -- which defaults to
    // inherit: true -- finds two attributes on a migration hierarchy carrying [DbContext] at two
    // levels and throws AmbiguousMatchException, making the replacement strictly LESS tolerant than
    // the base class it replaces on that one input. The plural GetCustomAttributes + FirstOrDefault
    // rather than the singular overload even at one level, because AllowMultiple=True means two
    // attributes on ONE type are legal too and would throw the same way. ROADMAP 0.3 Phase 14
    // deferred Minor (b).
    private static Type? DeclaredContextType(Type? candidate)
    {
        for (Type? type = candidate; type is not null; type = type.BaseType)
        {
            if (type.GetCustomAttributes<DbContextAttribute>(inherit: false).FirstOrDefault() is { } attribute)
            {
                return attribute.ContextType;
            }
        }

        return null;
    }

    // Entity Framework Core's own equivalent lives in an internal extension method. Spelling it out
    // here avoids taking a dependency on a second internal API for four tokens of filtering.
    private IEnumerable<TypeInfo> ConstructibleTypes() =>
        LoadableDefinedTypes().Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition);

    // Mirrors Entity Framework Core's own fallback (Assembly.GetLoadableDefinedTypes(), in
    // src/Shared/SharedTypeExtensions.cs, which the stock MigrationsAssembly this type replaces relies
    // on via GetConstructibleTypes()): one type in the migrations assembly that Entity Framework Core
    // or its host process cannot load must not take every OTHER migration in that assembly down with
    // it. Reproduced here rather than called -- calling Entity Framework Core's own extension would be
    // a second EF1001 site for an internal API, where reproducing this ten-line catch clause keeps the
    // suppression to the one type this file already derives from.
    //
    // Not logged. Entity Framework Core's own call site (MigrationsAssembly.Migrations) does not pass
    // a logger to this fallback either -- the only public logging extension for this event,
    // CoreLoggerExtensions.TypeLoadingErrorWarning, is keyed to IDiagnosticsLogger<DbLoggerCategory.Model>,
    // a different category than the IDiagnosticsLogger<DbLoggerCategory.Migrations> this type is
    // constructed with, and Entity Framework Core does not thread a second logger through this path
    // either. Adding one here would mean a second constructor dependency for a diagnostic Entity
    // Framework Core itself does not surface at this call site.
    private IEnumerable<TypeInfo> LoadableDefinedTypes()
    {
        try
        {
            return Assembly.DefinedTypes;
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Select(type => type!.GetTypeInfo());
        }
    }
}

#pragma warning restore EF1001
