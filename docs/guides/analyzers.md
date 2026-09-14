# Analyzers

`Statesman.Analyzers` helps preserve one state authority while allowing deliberate migration boundaries. The package also ships code fixes for the shapes that have a safe automated repair; there is no separate code-fix package to install.

## STM001: direct managed-state mutation

Reports assignments, compound assignments, increments, and decrements to fields or properties owned by a `[ManagedState]` type outside an allowed boundary.

Ownership is resolved by walking the receiver chain, not by asking only the immediately containing type. `state.Plain.Value = 1` is reported when `Plain` is declared on a `[ManagedState]` type, even though `Plain`'s own class carries no attribute: a plain object held by managed state is still managed state. The walk stops at the nearest link owned by a managed type, so the diagnostic names that type. A plain type's own constructor and its own methods writing its own members are reached by no managed chain and stay silent.

"Plain object" includes a framework type a managed member happens to hold, so `state.Builder.Length = 0` on a held `StringBuilder` is reported; only assignment-shaped mutation of such a type is caught, because `state.Builder.Append('x')` is a method call and STM004 analyses only `ICollection<T>`-shaped receivers. An indexer assignment on a collection reached through managed state is STM004's alone and is not also reported here: `state.Map["k"] = 1` resolves to the dictionary's own indexer, whose declaring type is not managed state. A `[ManagedState]` type's *own* indexer setter is still reported by this rule, because no other rule covers it.

Allowed locations include the managed type's constructor, object and `with` initializers, and symbols marked `[StateMutationBoundary]` or `[StateMutationAnalysisIgnore]`. Note that it is the managed type's *constructor* specifically: a write from another member of the managed type itself, including from inside one of its own property setters, is reported. An escape hatch also silences everything reached *through* what it marks: a type-level or member-level `[StateMutationAnalysisIgnore]` stops the ownership walk there, so a write behind that type or member is silent rather than re-attributed to an enclosing managed type. This applies to STM004 on the same terms.

**How to fix.** Route the write through `IState<T>.SetAsync` or `UpdateAsync`, move it into a declared interaction reducer, or, when a legacy adapter genuinely owns the write, mark the enclosing method `[StateMutationBoundary("reason")]`. There is deliberately no code fix: every repair is a design choice the analyzer cannot make, and the two mechanical candidates are both suppressions with a mandatory human-supplied reason. A code fix that inserted one for you would make the escape hatch the path of least resistance.

## STM002: publicly mutable managed-state shape

Reports public non-readonly fields and public non-init property setters on `[ManagedState]` types. Immutable records are the easiest compliant shape.

Prefer `IReadOnlyList<T>`, `IReadOnlyDictionary<TKey, TValue>` or `ImmutableArray<T>` for a collection-typed member. A `List<T>` member is not itself reported, because the type is a legitimate choice behind a boundary, but mutating it in place is, by STM004 below, at the site that performs the mutation. Every `[ManagedState]` declaration in this repository's own samples, tests and documentation is a positional `record` whose collection members are read-only interfaces.

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

The rule uses the same receiver-chain walk and the same boundaries as STM001, and the same exemptions apply, including a null-conditional receiver: `state.Plain?.Items.Add(1)` reports, and the message names the mutation as written. Four behaviours are worth stating:

- A member that returns a new collection rather than mutating the receiver is silent. `ImmutableList<T>.Add` does not fire. `ImmutableList<T>.Builder.Add` does, because a builder mutates in place.
- A read is silent. `Count`, `Contains` and enumeration are not mutations, and neither is mutating a copy such as `state.Items.ToList().Add(1)`.
- The mutation is attributed to the nearest managed owner on the chain. `state.Child.Items.Add(1)`, where `Child`'s own type carries `[ManagedState]`, names `Child`'s type; `state.Plain.Items.Add(1)`, where `Plain`'s type does not, names the outer managed type.
- The mutator set is `ICollection<T>`-shaped. A member typed as `Queue<T>` or `Stack<T>`, which implement only the non-generic `ICollection`, or as a non-generic collection such as `IList`, `IDictionary` or `Hashtable`, is not analysed, and `Enqueue`, `Push`, `Dequeue` and `Pop` are not in the mutator set. That is a scope limit recorded in the design spec, not a judgement that those mutations are safe.

**How to fix.** Build the new collection and write the whole state value through `IState<T>`, or perform the mutation inside a declared reducer or a `[StateMutationBoundary("reason")]` member. There is no code fix: replacing an in-place mutation with an authoritative write is a rewrite of the surrounding method, not a member-level edit.

## Boundary attributes

`[StateMutationBoundary("reason")]` documents a reducer, serializer, mapper, or temporary legacy bridge that intentionally mutates a managed type.

`[StateMutationAnalysisIgnore("reason")]` is a narrower escape hatch. Keep the reason specific and configure code review to reject blanket suppression.

The analyzer cannot prove every alias, reflection write, unsafe write, or mutation performed inside an external assembly. Treat it as an architectural guardrail, not a runtime security mechanism. The alias limit is worth naming precisely, because it is the one shape STM004 looks like it should catch and does not: there is no data flow and no alias tracking, so `var items = state.Items; items.Add(1)` is silent by design. Closing it would need a local's initializer to be re-walked and every reassignment, `out`, `ref` and lambda capture of that local accounted for, which is a larger rule with a larger false-positive surface.
