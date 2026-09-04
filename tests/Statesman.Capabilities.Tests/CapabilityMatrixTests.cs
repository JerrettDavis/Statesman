using System.Reflection;

namespace Statesman.Capabilities.Tests;

public sealed class CapabilityMatrixTests
{
    private static readonly (Type StoreType, string ColumnHeader)[] Providers =
    [
        (typeof(InMemoryStateLedgerStore), "InMemory"),
        (typeof(FileSystemStateLedgerStore), "FileSystem"),
        (typeof(RedisStateLedgerStore), "Redis"),
        (typeof(EntityFrameworkStateLedgerStore<>), "EntityFrameworkCore"),
        (typeof(TieredStateLedgerStore), "Tiered"),
    ];

    private static readonly (Type CapabilityType, string RowLabel)[] Capabilities =
    [
        (typeof(IStateLedgerReplica), "IStateLedgerReplica"),
        (typeof(IStateLeaseProvider), "IStateLeaseProvider"),
        (typeof(IStateChangeFeed), "IStateChangeFeed"),
        (typeof(IPartitionCatalog), "IPartitionCatalog"),
        (typeof(IDistributedCapture), "IDistributedCapture"),
        (typeof(IReplicationLagSource), "IReplicationLagSource"),
    ];

    [Fact]
    public void Capability_matrix_doc_matches_reflection()
    {
        string docPath = FindCapabilitiesDoc();
        string[] lines = File.ReadAllLines(docPath);

        string? headerLine = Array.Find(lines, l => l.TrimStart().StartsWith("| Capability"));
        Assert.True(headerLine is not null,
            "capabilities.md must contain a table header starting with '| Capability'.");
        string[] headerColumns = SplitRow(headerLine!);

        foreach ((Type capabilityType, string rowLabel) in Capabilities)
        {
            string? row = Array.Find(lines, l => SplitRow(l) is [var first, ..] && first == rowLabel);
            Assert.True(row is not null, $"capabilities.md is missing a row for {rowLabel}.");
            string[] cells = SplitRow(row!);

            foreach ((Type storeType, string columnHeader) in Providers)
            {
                int columnIndex = Array.IndexOf(headerColumns, columnHeader);
                Assert.True(columnIndex >= 0, $"capabilities.md is missing a column for {columnHeader}.");
                Assert.True(columnIndex < cells.Length,
                    $"capabilities.md row for {rowLabel} has no cell for {columnHeader}.");

                bool documented = cells[columnIndex] switch
                {
                    "Yes" => true,
                    "No" => false,
                    var other => throw new InvalidOperationException(
                        $"Unexpected cell value '{other}' for {rowLabel}/{columnHeader}; expected Yes or No."),
                };

                bool actual = storeType.GetInterfaces().Contains(capabilityType);

                Assert.True(documented == actual,
                    $"{columnHeader} {(actual ? "implements" : "does not implement")} {rowLabel} " +
                    $"but capabilities.md says {(documented ? "Yes" : "No")}.");
            }
        }
    }

    [Fact]
    public void Capability_and_provider_lists_are_complete()
    {
        Type[] allCapabilityInterfaces = [.. typeof(IStateCapability).Assembly.GetTypes()
            .Where(t => t.IsInterface
                && t != typeof(IStateCapability)
                && typeof(IStateCapability).IsAssignableFrom(t))];

        Assert.True(
            allCapabilityInterfaces.OrderBy(t => t.Name).SequenceEqual(
                Capabilities.Select(c => c.CapabilityType).OrderBy(t => t.Name)),
            $"Capabilities array is out of sync with IStateCapability implementers in " +
            $"{typeof(IStateCapability).Assembly.GetName().Name}. Found: " +
            $"[{string.Join(", ", allCapabilityInterfaces.Select(t => t.Name))}], declared: " +
            $"[{string.Join(", ", Capabilities.Select(c => c.RowLabel))}].");

        Assembly[] providerAssemblies = [.. Providers.Select(p => p.StoreType.Assembly).Distinct()];

        Type[] allProviderTypes = [.. providerAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass
                && t.IsPublic
                && !t.IsAbstract
                && t.GetInterfaces().Contains(typeof(IStateLedgerStore)))];

        Assert.True(
            allProviderTypes.OrderBy(t => t.Name).SequenceEqual(
                Providers.Select(p => p.StoreType).OrderBy(t => t.Name)),
            $"Providers array is out of sync with IStateLedgerStore implementations across the " +
            $"referenced provider assemblies. Found: " +
            $"[{string.Join(", ", allProviderTypes.Select(t => t.Name))}], declared: " +
            $"[{string.Join(", ", Providers.Select(p => p.ColumnHeader))}].");
    }

    private static string[] SplitRow(string line) =>
        [.. line.Trim().Trim('|').Split('|').Select(cell => cell.Trim().Trim('`'))];

    private static string FindCapabilitiesDoc()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Statesman.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(directory!.FullName, "docs", "architecture", "capabilities.md");
    }
}
