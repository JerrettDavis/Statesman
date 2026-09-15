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
        using System.Collections;
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.Collections.ObjectModel;
        using System.Linq;
        using System.Text;

        namespace Statesman
        {
            [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
            public sealed class ManagedStateAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Constructor)]
            public sealed class StateMutationBoundaryAttribute : Attribute { }
            [AttributeUsage(AttributeTargets.All)]
            public sealed class StateMutationAnalysisIgnoreAttribute : Attribute { }
        }

        namespace Decoy
        {
            // A look-alike ICollection<T> in the wrong namespace. STM004's interface test is
            // namespace qualified, so a type implementing only this one must stay silent.
            public interface ICollection<T>
            {
                void Add(T item);
            }
        }

        public sealed class DecoyBag : Decoy.ICollection<int>
        {
            public void Add(int item)
            {
            }
        }

        [Statesman.ManagedState]
        public sealed class ChildBag
        {
            public int Value { get; set; }
            public List<int> Items { get; init; } = new();

            [Statesman.StateMutationAnalysisIgnore]
            public PlainBag Hidden { get; init; } = new();
        }

        [Statesman.ManagedState]
        [Statesman.StateMutationAnalysisIgnore]
        public sealed class IgnoredManaged
        {
            public int Value { get; set; }
            public List<int> Items { get; init; } = new();
        }

        [Statesman.ManagedState]
        public sealed record Rec(int Value);

        // A collection whose own indexer returns by reference, so the indexer has no set accessor at
        // all while the type still implements ICollection<int>. `s.Refs[0] = 1` compiles.
        public sealed class RefSlots : List<int>
        {
            private readonly int[] _values = new int[4];

            public new ref int this[int index] => ref _values[index];
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

            // A user-defined explicit conversion, whose invocation is a static method call that can
            // return any object it likes rather than a view onto this one. The cast arm must stop
            // the walk here instead of treating it as identity-preserving. ROADMAP 0.3 Phase 21,
            // review finding on the ownership walk's conversion arms.
            public static explicit operator Exported(PlainBag bag) => new Exported();
        }

        // The unrelated object a user-defined conversion can return. It carries the same member
        // shapes as PlainBag on purpose, so a write reaching it through the conversion is
        // indistinguishable from one reaching PlainBag except for which object it actually lands on.
        public sealed class Exported
        {
            public int Value { get; set; }
            public List<int> Items { get; init; } = new();
        }

        // A collection with a user-defined increment operator, so `local++` genuinely rebinds a
        // local that holds one. Nothing else in the fixture can express that shape.
        public sealed class Countable : List<int>
        {
            public static Countable operator ++(Countable value) => new();
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
            public PlainBag[] Plains { get; init; } = new PlainBag[2];
            public ICollection<int> Coll { get; init; } = new List<int>();
            public DecoyBag Decoyed { get; init; } = new();
            public RefSlots Refs { get; init; } = new();
            public IgnoredManaged Ig { get; init; } = new();
            public int Scalar { get; set; }

            // Non-generic and concurrent collections. Every one of these implements
            // System.Collections.ICollection and NOT ICollection<T>, so only the non-generic
            // operand of IsCollectionInterface reaches them. ROADMAP 0.3 Phase 21.
            public Queue<int> Fifo { get; init; } = new();
            public Stack<int> Lifo { get; init; } = new();
            public LinkedList<int> Linked { get; init; } = new();
            public ConcurrentQueue<int> ConcurrentFifo { get; init; } = new();
            public ConcurrentBag<int> ConcurrentUnordered { get; init; } = new();
            public BlockingCollection<int> Blocking { get; init; } = new();
            public ArrayList Legacy { get; init; } = new();
            public Hashtable LegacyMap { get; init; } = new();
            public IList LegacyList { get; init; } = new ArrayList();
            public ICollection LegacyCollection { get; init; } = new ArrayList();

            // Types that pass the type test today and whose mutators are only in the name set
            // this phase adds. These are what make the name boundary visible.
            public ObservableCollection<int> Observable { get; init; } = new();
            public SortedSet<int> Sorted { get; init; } = new();
            public ConcurrentDictionary<string, int> ConcurrentMap { get; init; } = new();

            // Not a collection at all: neither ICollection<T> nor System.Collections.ICollection,
            // so Clear and Append stay silent even though Clear is in the mutator set.
            public StringBuilder Builder { get; init; } = new();
            public Countable Counted { get; init; } = new();

            // The managed type's OWN indexer. STM001 owns this shape, because no other rule covers
            // it and STM004 does not: Bag is not a collection.
            public int this[int index]
            {
                get => 0;
                set { }
            }

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

            // Reached only by the alias rows: `Rebind` is the out-parameter bail-out's call site
            // and `Capture` is the lambda row's. Both are deliberately unused by every other body.
            private static void Rebind(out List<int> target) => target = new List<int>();

            private static void Capture(Action action)
            {
            }
        }
        """;
}
