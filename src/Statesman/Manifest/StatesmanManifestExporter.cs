using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Statesman;

public static class StatesmanManifestExporter
{
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(indented: false);
    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(indented: true);

    public static string ToJson(StatesmanManifest manifest, bool indented = true) =>
        JsonSerializer.Serialize(manifest, indented ? IndentedOptions : CompactOptions);

    public static string ToMermaid(StatesmanManifest manifest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("flowchart LR");
        builder.AppendLine($"  root[\"Statesman: {Escape(manifest.Id)}\\n{manifest.Version}\"]");

        foreach (StateContainerManifest container in manifest.Containers.Where(value => !value.Path.IsRoot))
        {
            string containerId = Id("container_" + container.Path.Value);
            string boundary = container.Isolation == StateContainerIsolation.Isolated ? "isolated" : "attached";
            builder.AppendLine($"  {containerId}[\"{Escape(container.Path.Value)}\\n({boundary})\"]");
            StateContainerManifest? parent = manifest.Containers
                .Where(candidate => candidate.Path != container.Path &&
                    (candidate.Path.IsRoot || container.Path.IsDescendantOf(candidate.Path)))
                .OrderByDescending(candidate => candidate.Path.Value.Length)
                .FirstOrDefault();
            string parentId = parent is null || parent.Path.IsRoot
                ? "root"
                : Id("container_" + parent.Path.Value);
            builder.AppendLine($"  {parentId} --> {containerId}");
        }

        foreach (StateDefinitionManifest state in manifest.States)
        {
            string stateId = Id("state_" + state.Path.Value);
            string containerPath = manifest.Containers
                .Where(container => !container.Path.IsRoot && state.Path.IsDescendantOf(container.Path))
                .OrderByDescending(container => container.Path.Value.Length)
                .Select(container => container.Path.Value)
                .FirstOrDefault() ?? string.Empty;
            string parentId = containerPath.Length == 0 ? "root" : Id("container_" + containerPath);
            string partitioned = state.IsPartitioned ? "partitioned" : "singleton";
            builder.AppendLine($"  {stateId}([\"{Escape(state.Path.Value)}\\n{Escape(ShortType(state.ValueType))} · {partitioned}\"])");
            builder.AppendLine($"  {parentId} --> {stateId}");

            foreach (StateSourceManifest source in state.Sources)
            {
                string sourceId = Id($"source_{state.Path.Value}_{source.Name}");
                builder.AppendLine($"  {sourceId}{{\"{Escape(source.Name)}\"}}");
                builder.AppendLine($"  {sourceId} --> {stateId}");
            }
        }

        return builder.ToString();
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string Id(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            output.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return output.ToString();
    }

    private static string Escape(string value) => value.Replace("\"", "'", StringComparison.Ordinal);

    private static string ShortType(string value)
    {
        int index = value.LastIndexOf('.');
        return index < 0 ? value : value[(index + 1)..];
    }
}
