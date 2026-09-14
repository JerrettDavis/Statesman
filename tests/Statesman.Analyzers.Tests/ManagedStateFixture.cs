namespace Statesman.Analyzers.Tests;

/// <summary>
/// One managed type holding every shape STM001 and STM004 have to rule on, plus a managed child, a
/// plain child and an unmanaged look-alike. Shared by both analyzers' test classes so the two rules
/// are measured against exactly the same declarations — they share one definition of ownership
/// (<c>ManagedStateOwnership.ResolveManagedStateOwner</c>), so they must share one fixture.
/// </summary>
internal static class ManagedStateFixture
{
    /// <summary>The declarations every case below is compiled against.</summary>
    public const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.Linq;

        namespace Statesman
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            public sealed class ManagedStateAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Constructor)]
            public sealed class StateMutationBoundaryAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.All)]
            public sealed class StateMutationAnalysisIgnoreAttribute : Attribute { }
        }

        [Statesman.ManagedState]
        public sealed class ChildBag
        {
            public int Value { get; set; }
            public List<int> Items { get; init; } = new();
        }

        public sealed class PlainBag
        {
            public int Value { get; set; }
            public List<int> Items { get; init; } = new();

            [Statesman.StateMutationAnalysisIgnore]
            public int Exempt { get; set; }

            public PlainBag() => Value = 0;

            public void Reset()
            {
                Value = 0;
                Items.Clear();
            }
        }

        public sealed class Unmanaged
        {
            public List<int> Items { get; init; } = new();
        }

        [Statesman.ManagedState]
        public sealed class Bag
        {
            public List<int> Items { get; init; } = new();
            public Dictionary<string, int> Map { get; init; } = new();
            public HashSet<int> Set { get; init; } = new();
            public ImmutableList<int> Frozen { get; init; } = ImmutableList<int>.Empty;
            public ImmutableList<int>.Builder FrozenBuilder { get; init; } = ImmutableList.CreateBuilder<int>();
            public IReadOnlyList<int> Readonlies { get; init; } = Array.Empty<int>();
            public int[] Slots { get; init; } = new int[4];
            public ChildBag Child { get; init; } = new();
            public PlainBag Plain { get; init; } = new();
            public int Scalar { get; set; }

            [Statesman.StateMutationAnalysisIgnore]
            public PlainBag Hidden { get; init; } = new();

            [Statesman.StateMutationAnalysisIgnore]
            public List<int> Ignored { get; init; } = new();
        }

        """;

    /// <summary>
    /// The fixture plus one consumer body, wrapped in a static method that receives a managed
    /// <c>Bag</c> and an unmanaged <c>Unmanaged</c>.
    /// </summary>
    /// <param name="consumerBody">The statement or statements to place in the consumer.</param>
    /// <returns>A complete compilable source.</returns>
    public static string Consumer(string consumerBody) => Source + $$"""
        public static class Consumer
        {
            public static void Run(Bag s, Unmanaged p)
            {
                {{consumerBody}}
            }
        }
        """;
}
