namespace Statesman;

/// <summary>Entry point for declaring an authoritative state space.</summary>
public static class Statesman
{
    public static StatesmanDeclarationBuilder Declare(string id, string version = "1.0") =>
        new(id, version);
}
