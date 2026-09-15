# Analyzers

`Statesman.Analyzers` helps preserve one state authority while allowing deliberate migration boundaries. The package also ships code fixes for the shapes that have a safe automated repair; there is no separate code-fix package to install.

## STM001: direct managed-state mutation

Reports assignments, compound assignments, increments, and decrements to fields or properties owned by a `[ManagedState]` type outside an allowed boundary.

Ownership is resolved by walking the receiver chain, not by asking only the immediately containing type. `state.Plain.Value = 1` is reported when `Plain` is declared on a `[ManagedState]` type, even though `Plain`'s own class carries no attribute: a plain object held by managed state is still managed state. The walk stops at the nearest link owned by a managed type, so the diagnostic names that type. A plain type's own constructor and its own methods writing its own members are reached by no managed chain and stay silent.

"Plain object" includes a framework type a managed member happens to hold, so `state.Builder.Length = 0` on a held `StringBuilder` is reported; only assignment-shaped mutation of such a type is caught, because `state.Builder.Append('x')` is a method call and STM004 analyses only `ICollection<T>`-shaped receivers. An indexer assignment on a collection reached through managed state is STM004's alone and is not also reported here: `state.Map["k"] = 1` resolves to the dictionary's own indexer, whose declaring type is not managed state. A `[ManagedState]` type's *own* indexer setter is still reported by this rule, because no other rule covers it.

Allowed locations include the managed type's constructor, object and `with` initializers, and symbols marked `[StateMutationBoundary]` or `[StateMutationAnalysisIgnore]`. Note that it is the managed type's *constructor* specifically: a write from another member of the managed type itself, including from inside one of its own property setters, is reported. An escape hatch also silences everything reached *through* what it marks: a type-level or member-level `[StateMutationAnalysisIgnore]` stops the ownership walk there, so a write behind that type or member is silent rather than re-attributed to an enclosing managed type. This applies to STM004 on the same terms.

Adding or removing an event handler is not a mutation for either rule, and the silence is deliberate rather than an oversight: `state.Changed += handler` does not change the value the ledger stores, and reporting it would fire on every idiomatic observer wiring. The asymmetry is worth stating because it looks like a bug from the outside — `state.Plain.Value = 1` on a plain holder is reported while `state.Plain.Changed += handler` on the same holder is not. Two mechanisms produce it: STM001's symbol test admits only properties and fields, and the ownership walk both rules share attributes a mutation only to a property or a field. Both are pinned by tests, so neither can change by accident.

**How to fix.** Route the write through `IState<T>.SetAsync` or `UpdateAsync`, move it into a declared interaction reducer, or, when a legacy adapter genuinely owns the write, mark the enclosing method `[StateMutationBoundary("reason")]`. There is deliberately no code fix: every repair is a design choice the analyzer cannot make, and the two mechanical candidates are both suppressions with a mandatory human-supplied reason. A code fix that inserted one for you would make the escape hatch the path of least resistance.

## STM002: publicly mutable managed-state shape

Reports public non-readonly fields and public non-init property setters on `[ManagedState]` types. Immutable records are the easiest compliant shape.

Prefer `IReadOnlyList<T>`, `IReadOnlyDictionary<TKey, TValue>` or `ImmutableArray<T>` for a collection-typed member. A `List<T>` member is not itself reported, because the type is a legitimate choice behind a boundary, but mutating it in place is, by STM004 below, at the site that performs the mutation. Every `[ManagedState]` declaration in this repository's own samples and documentation is a positional
`record` whose collection members are read-only interfaces. The analyzer's own test fixtures are the
deliberate exception: they declare `List<int>`, `Dictionary<string, int>` and `HashSet<int>` members
precisely so the rules can be measured against them.

**How to fix.** Two code fixes are offered, and both support fix-all across a document, project or solution:

- *Make this property init-only* rewrites `{ get; set; }` to `{ get; init; }`. A setter with a block or expression body is deliberately left alone: swapping the keyword changes when that body may run, not only who may call it.
- *Make this field readonly* adds `readonly` to a single-variable field declaration. A `const` field is already immutable and is never reported; a multi-variable declaration is reported but not fixed, because `readonly` would apply to every declarator rather than the reported one; and a `volatile` field is reported but not fixed, because a field cannot be both `volatile` and `readonly` (CS0678), so the fix would emit code that does not compile.

Two consequences are worth knowing before applying either fix. `init` is a C# 9 feature that needs `System.Runtime.CompilerServices.IsExternalInit`, which `netstandard2.0` does not carry, so a `netstandard2.0` project must declare its own one-line `internal static class IsExternalInit { }` or the fixed code fails with CS0518. And making a field `readonly` can turn an STM001 warning at a write site into a CS0191 compile error, because the write is no longer legal at all: louder and more correct, but a build break rather than a warning, so preview the fix before applying it broadly.

## STM003: dynamic state key

Reports `StateKey.Define<T>` calls whose path is not a compile-time string constant. Runtime partitions are the correct place for dynamic identity.

**How to fix.** Pass any shape the compiler can fold to a constant. All of these are already accepted and never reported: a string literal, a concatenation of literals, a `const string` field, `nameof(...)`, and an interpolated string whose every hole is constant. The rule fires only on something genuinely unfoldable, such as a `static readonly string`, a method parameter, a method call, `string.Concat`, or `null`. There is no code fix, because the only mechanically repairable shape, `static readonly string` to `const string`, edits a different member, possibly in a different file, and is correct only when that field's initializer is itself constant and nothing else assigns it.

## STM004: managed state collection mutation

Reports an in-place mutation of a collection reached through a `[ManagedState]` type: a call to a mutating member such as `Add`, `Remove`, `Clear`, `Insert`, `Sort` or `UnionWith`, an indexer assignment such as `state.Map["k"] = 1`, or an array element assignment such as `state.Slots[0] = 1`. A collection reached through managed state is still managed state; mutating it in place creates a second source of truth that bypasses the ledger and its observers.

It is a separate id rather than a widening of STM001 on purpose. STM001 is documented, and recommended for ratcheting to error during migration, as being about *assignment*; a consumer who suppressed it made a decision about assignments, not about `List<T>.Add`. The two rules do share one definition of ownership, so they agree on what "reached through managed state" means.

The rule uses the same receiver-chain walk and the same boundaries as STM001, and the same exemptions apply, including a null-conditional receiver: `state.Plain?.Items.Add(1)` reports, and the message names the mutation as written. The walk also steps through a conversion on the chain, except a user-defined conversion: `((List<int>)state.Coll).Add(1)` and `(state.Coll as List<int>).Add(1)` are reported where they used to be silent, but a user-defined explicit conversion operator, whose result is a different object, stops the walk there. It also follows a local alias one hop per identifier, applied each time the walk lands on an identifier, so `var items = state.Items; items.Add(1)` is reported, and so is a chain such as `var items = state.Items; var copy = items; copy.Add(1);`. Four behaviours are worth stating:

- A member that returns a new collection rather than mutating the receiver is silent. `ImmutableList<T>.Add` does not fire. `ImmutableList<T>.Builder.Add` does, because a builder mutates in place.
- A read is silent. `Count`, `Contains` and enumeration are not mutations, and neither is mutating a copy such as `state.Items.ToList().Add(1)`.
- The mutation is attributed to the nearest managed owner on the chain. `state.Child.Items.Add(1)`, where `Child`'s own type carries `[ManagedState]`, names `Child`'s type; `state.Plain.Items.Add(1)`, where `Plain`'s type does not, names the outer managed type.
- The boundary is the type **and** the member name, and both are wider than they were. A type is in
  scope when it implements (or is) `ICollection<T>` or the non-generic `System.Collections.ICollection`
  and is not a top-level immutable collection, which covers `List<T>`, `Dictionary<,>`, `HashSet<T>`,
  `SortedSet<T>`, `SortedList<,>`, `LinkedList<T>`, `Collection<T>`, `ObservableCollection<T>`,
  `Queue<T>`, `Stack<T>`, every `System.Collections.Concurrent` collection, `BlockingCollection<T>`,
  the immutable builders, arrays, and the legacy `ArrayList`, `Hashtable`, `BitArray`, `IList`,
  `IDictionary`, `StringCollection` and `NameValueCollection`. The member name must be one of
  forty-two, which now includes `Enqueue`, `Dequeue`, `Push`, `Pop`, `AddFirst`, `AddLast`,
  `RemoveFirst`, `RemoveLast`, `Move`, `RemoveWhere`, `GetOrAdd`, `AddOrUpdate`, `TryRemove`,
  `TryTake`, `TryDequeue`, `TryPop` and `CompleteAdding`. The name test is what keeps an ambiguous
  name safe on a type that is not a collection: `state.Items.Take(2)` resolves to `Enumerable.Take`
  and is not reported. What remains out of scope is a mutating member whose name is not in that set —
  `CopyTo` writes its argument rather than its receiver and is deliberately absent — and any type that
  implements neither collection interface, such as `StringBuilder` or `Memory<T>`.

**How to fix.** Build the new collection and write the whole state value through `IState<T>`, or perform the mutation inside a declared reducer or a `[StateMutationBoundary("reason")]` member. There is no code fix: replacing an in-place mutation with an authoritative write is a rewrite of the surrounding method, not a member-level edit.

## Boundary attributes

`[StateMutationBoundary("reason")]` documents a reducer, serializer, mapper, or temporary legacy bridge that intentionally mutates a managed type.

`[StateMutationAnalysisIgnore("reason")]` is a narrower escape hatch. Keep the reason specific and configure code review to reject blanket suppression.

The analyzer cannot prove every alias, reflection write, unsafe write, or mutation performed inside an
external assembly. Treat it as an architectural guardrail, not a runtime security mechanism. The alias
limit is narrower than it was: a local alias is now followed one hop per identifier, applied each time
the walk lands on an identifier, so `var items = state.Items; items.Add(1)` is reported, and so is a
chain such as `var items = state.Items; var copy = items; copy.Add(1);`. Rebinding the alias is one of
the bail-outs that keeps this from over-reaching: a reassignment, an `out`/`ref` argument, an increment
or decrement, and a deconstructing assignment into an existing local such as
`(items, _) = (new List<int>(), 0);` all count as a rebind and stay silent. What is still out of scope
is anything that needs data flow rather than syntax — an alias stored in a field, one returned from a
method, and a `foreach`, pattern or declaration-time deconstruction variable, none of which has an
initializer to follow. Each of those is silent by design and pinned by a test, so the boundary is a
decision rather than an accident.
