using System.Globalization;
using System.Text;

namespace Statesman.FileSystem.Tests;

/// <summary>
/// The in-process change-log index added in ROADMAP 0.3 Phase 11, and the four ways it can be wrong.
/// </summary>
/// <remarks>
/// Every test here is written against a mechanism that is invisible from outside the class: the index
/// is private state with no accessor, and this repository has zero <c>[InternalsVisibleTo]</c> and
/// keeps it. So each test observes the index only through what <c>ReadAsync</c> yields after the
/// bytes on disk have been changed behind it — which is also the only thing a caller can observe, and
/// therefore the only thing worth pinning. Four of the five fail against a deliberately broken
/// mechanism; each says which check it is the proof of.
/// </remarks>
public sealed class FileSystemChangeFeedIndexTests
{
    [Fact]
    public async Task A_shrunk_change_log_invalidates_the_index_rather_than_serving_lines_that_are_gone()
    {
        // Break-the-mechanism proof for the file-length check. The index remembers a byte offset and
        // every entry below it; if that memo survived a truncation, this read would keep yielding
        // records whose lines no longer exist. The history files are all still present, so a stale
        // index yields three and the assertion below reads 3 instead of 1.
        //
        // Truncation is not hypothetical: it is the documented recovery from a corrupt change log,
        // and ROADMAP.md lists filesystem compaction tooling as unshipped rather than impossible.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            StateAppendResult first = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("a3"));

            // Warm the index.
            Assert.Equal(3, (await DrainAsync(store, from: null)).Count);

            string log = Path.Combine(directory, "_changes.log");
            string[] lines = await File.ReadAllLinesAsync(log);
            Assert.Equal(3, lines.Length);
            await File.WriteAllTextAsync(log, lines[0] + "\n");

            List<StateChangeEnvelope> afterShrink = await DrainAsync(store, from: null);

            StateChangeEnvelope only = Assert.Single(afterShrink);
            Assert.Equal(first.Record!.GlobalPosition, only.Record.GlobalPosition);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_same_length_rewrite_of_the_last_line_invalidates_the_index_too()
    {
        // Break-the-mechanism proof for the tail-bytes check, which is the half a length comparison
        // cannot do. The last line is replaced in place by another VALID line of exactly the same
        // byte length naming a different existing record, so the file's length is unchanged and only
        // the remembered tail bytes differ. Without the tail check the index keeps the old entry and
        // the third record reads as address A revision 2 instead of address B revision 1.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });

            // Equal-length paths, so the rewritten line is byte-for-byte the same length.
            var addressA = new StateAddress("app", "feed/aa", StatePartition.Default);
            var addressB = new StateAddress("app", "feed/bb", StatePartition.Default);
            await store.AppendAsync(addressA, StateWriteCondition.Absent, Commit("a1"));
            await store.AppendAsync(addressB, StateWriteCondition.Absent, Commit("b1"));
            StateAppendResult third = await store.AppendAsync(addressA, StateWriteCondition.AtRevision(1), Commit("a2"));

            List<StateChangeEnvelope> warm = await DrainAsync(store, from: null);
            Assert.Equal(3, warm.Count);
            Assert.Equal(addressA.Canonical, warm[2].Record.Address.Canonical);
            Assert.Equal(2L, warm[2].Record.Revision);

            string log = Path.Combine(directory, "_changes.log");
            string[] lines = await File.ReadAllLinesAsync(log);
            Assert.Equal(3, lines.Length);

            // Same position, same root, same partition; address B and revision 1 instead of A and 2.
            string rewritten = string.Join(
                '\t',
                third.Record!.GlobalPosition.ToString(CultureInfo.InvariantCulture),
                addressB.Root,
                addressB.Path.Value,
                addressB.Partition.Value,
                "1");
            Assert.Equal(lines[2].Length, rewritten.Length);
            await File.WriteAllTextAsync(log, string.Join('\n', lines[0], lines[1], rewritten) + "\n");

            List<StateChangeEnvelope> afterRewrite = await DrainAsync(store, from: null);

            Assert.Equal(3, afterRewrite.Count);
            Assert.Equal(addressB.Canonical, afterRewrite[2].Record.Address.Canonical);
            Assert.Equal(1L, afterRewrite[2].Record.Revision);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task An_import_below_the_warm_index_is_placed_in_position_order_not_appended()
    {
        // The log is NOT position-ordered: ImportAsync appends a line carrying the imported record's
        // own GlobalPosition, which can be lower than positions already in the log, and restoring
        // into a non-empty target is a documented, supported scenario. An index that pushed each new
        // entry onto the end -- correct for every append, and the obvious implementation -- yields
        // the import last here, and the first assertion reads the appended record instead.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            var imported = new StateAddress("app", "feed/restored", StatePartition.Default);

            StateAppendResult first = await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"));
            StateAppendResult second = await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"));

            // Warm the index on the two appends, so the import lands in a suffix scan.
            Assert.Equal(2, (await DrainAsync(store, from: null)).Count);

            // Derived from a real record so every required member and every validation rule is
            // satisfied without restating the shape here.
            StateRecord below = first.Record! with
            {
                Address = imported,
                Revision = 1,
                GlobalPosition = first.Record!.GlobalPosition - 1000,
            };
            await store.ImportAsync(below);

            List<StateChangeEnvelope> all = await DrainAsync(store, from: null);

            Assert.Equal(3, all.Count);
            Assert.Equal(
                [below.GlobalPosition, first.Record!.GlobalPosition, second.Record!.GlobalPosition],
                all.Select(envelope => envelope.Record.GlobalPosition).ToArray());
            Assert.Equal(imported.Canonical, all[0].Record.Address.Canonical);

            // ...and a cursor between the import and the appends resumes correctly, which is the
            // property the binary search in the copy loop has to get right.
            List<StateChangeEnvelope> after = await DrainAsync(store, new StateChangeCursor(below.GlobalPosition));
            Assert.Equal(
                [first.Record!.GlobalPosition, second.Record!.GlobalPosition],
                after.Select(envelope => envelope.Record.GlobalPosition).ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_torn_tail_after_an_incremental_read_is_never_half_consumed()
    {
        // Both torn-tail shapes, against a WARM index rather than a cold one -- which is the case the
        // pre-index reader never had to handle, because it re-parsed the whole file every time.
        //
        // In-flight shape first: a fragment with no terminator must be skipped and must NOT be
        // consumed into the index, so that when the rest of the line arrives the completed line is
        // parsed whole and yields exactly once. An index that consumed the fragment yields nothing
        // for it, ever; one that consumed it and then re-parsed the completed line yields it twice.
        //
        // Then the crash-durable shape: a garbage fragment followed by a real append, which starts a
        // fresh line rather than merging into it, leaves the garbage as a malformed line that is no
        // longer last -- and the read must report it, with the right 1-based line number counted
        // across two incremental scans rather than one full one.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/a", StatePartition.Default);
            await store.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"));
            await store.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"));

            string log = Path.Combine(directory, "_changes.log");
            Assert.Equal(2, (await DrainAsync(store, from: null)).Count);

            // A line in flight: the first three of its five fields, no terminator. The completion
            // below makes it name address A revision 1, an existing record, at a position above both.
            await File.AppendAllTextAsync(log, "900000000000000000\tapp\tfeed/a");
            Assert.Equal(2, (await DrainAsync(store, from: null)).Count);

            await File.AppendAllTextAsync(log, $"\t{StatePartition.Default.Value}\t1\n");
            List<StateChangeEnvelope> completed = await DrainAsync(store, from: null);
            Assert.Equal(3, completed.Count);

            // The record's own GlobalPosition is unaffected: it comes from the history file this
            // synthetic line points at (address A revision 1), written by the earlier real append,
            // and a duplicate change-log line never rewrites that file. What the synthetic line's
            // fabricated position sets is the cursor, per StateChangeCursor's documented equivalence
            // to a provider's own GlobalPosition -- so that is what proves the completed line was
            // parsed whole rather than dropped or mis-numbered.
            Assert.Equal(900000000000000000L, completed[2].Cursor.Position);
            Assert.Equal(1L, completed[2].Record.Revision);

            // A different proof for the same rule, against a fragment the field-short one above
            // cannot reach: one that already PARSES -- all five fields present -- but still lacks its
            // terminator. That is the shape a crash between writing a line's bytes and its trailing
            // newline leaves, and it is the only shape a mechanism that "inserts it when it parses"
            // can actually corrupt, because a field-short fragment never reaches that parse at all.
            //
            // The shipped rule this exercises is "a complete but unterminated final line yields" --
            // StreamReader's own behaviour, which the parser keeps -- so the fragment must already
            // count once, from the transient pending candidate. A mechanism that also inserts it into
            // the permanent index double-counts it in this same read: the index copy yields it once
            // and the pending merge appends it again, since both carry the identical position.
            var pendingAddress = new StateAddress("app", "feed/pending", StatePartition.Default);
            StateAppendResult warm = await store.AppendAsync(pendingAddress, StateWriteCondition.Absent, Commit("p1"));
            Assert.Equal(4, (await DrainAsync(store, from: null)).Count);

            long fragmentPosition = warm.Record!.GlobalPosition + 500;
            string fragmentLine = string.Join(
                '\t',
                fragmentPosition.ToString(CultureInfo.InvariantCulture),
                pendingAddress.Root,
                pendingAddress.Path.Value,
                pendingAddress.Partition.Value,
                "1");
            await File.AppendAllTextAsync(log, fragmentLine);
            Assert.Equal(5, (await DrainAsync(store, from: null)).Count);

            // Terminating it must not add a second record either: it is the SAME line, now durably
            // indexed instead of transiently pending.
            await File.AppendAllTextAsync(log, "\n");
            Assert.Equal(5, (await DrainAsync(store, from: null)).Count);

            // The crash-durable shape: a fragment that will never complete, then a real append, which
            // starts a fresh line and leaves the fragment as line 6 of 7.
            await File.AppendAllTextAsync(log, "garbage-with-no-fields");
            Assert.True((await store.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("a3"))).Succeeded);

            InvalidDataException thrown = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await DrainAsync(store, from: null));
            Assert.Contains("_changes.log", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("line 6", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_second_store_instance_over_one_directory_sees_the_first_instances_appends()
    {
        // The index is per store instance, so a reader whose writer is a different instance must
        // still pick up the appended suffix. This is the shape of a second process, minus the process
        // boundary: instance B's index is warm from its own first read and has to notice A's growth
        // through the file, because nothing in memory is shared between them.
        //
        // It is also the test that would fail if the refresh ever short-circuited on "this instance
        // has appended nothing since the last read", which is a tempting and wrong optimization.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileSystemStateLedgerStoreOptions { RootDirectory = directory };
            await using var writer = new FileSystemStateLedgerStore("feed", options);
            await using var reader = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/a", StatePartition.Default);

            await writer.AppendAsync(address, StateWriteCondition.Absent, Commit("a1"));
            Assert.Single(await DrainAsync(reader, from: null));

            await writer.AppendAsync(address, StateWriteCondition.AtRevision(1), Commit("a2"));
            await writer.AppendAsync(address, StateWriteCondition.AtRevision(2), Commit("a3"));

            List<StateChangeEnvelope> second = await DrainAsync(reader, from: null);

            Assert.Equal(3, second.Count);
            Assert.Equal([1L, 2L, 3L], second.Select(envelope => envelope.Record.Revision).ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_colliding_import_position_at_the_chunk_boundary_is_not_dropped()
    {
        // Final-review Critical 1. CopyChangeFeedEntriesUnsafe resumes the NEXT chunk strictly above
        // the LAST entry's position (ReadAsync's copiedThrough), which is correct only when a position
        // is never split across two chunks. Two index entries legitimately sharing one position -- a
        // colliding-lineage restore, which this repository documents as supported -- used to be split
        // exactly when the chunk boundary fell between them: the second entry is never revisited,
        // because the next chunk starts strictly above the very position it sits at. 256 appends to
        // one address fill exactly one ChangeFeedCopyChunk; importing a second lineage's record at the
        // 256th append's position put the dropped entry precisely on that boundary.
        string directory = Path.Combine(Path.GetTempPath(), "statesman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new FileSystemStateLedgerStore(
                "feed", new FileSystemStateLedgerStoreOptions { RootDirectory = directory });
            var address = new StateAddress("app", "feed/a", StatePartition.Default);

            var appended = new List<StateAppendResult>();
            for (int revision = 1; revision <= 256; revision++)
            {
                StateWriteCondition condition = revision == 1
                    ? StateWriteCondition.Absent
                    : StateWriteCondition.AtRevision(revision - 1);
                StateAppendResult result = await store.AppendAsync(address, condition, Commit($"v{revision}"));
                Assert.True(result.Succeeded);
                appended.Add(result);
            }

            // A second lineage's record, imported at EXACTLY the 256th append's position -- the
            // boundary CopyChangeFeedEntriesUnsafe's bounded loop stops at.
            var importedAddress = new StateAddress("app", "feed/imported", StatePartition.Default);
            StateRecord colliding = appended[255].Record! with
            {
                Address = importedAddress,
                Revision = 1,
                GlobalPosition = appended[255].Record!.GlobalPosition,
            };
            await store.ImportAsync(colliding);

            List<StateChangeEnvelope> all = await DrainAsync(store, from: null);
            Assert.Equal(257, all.Count);

            // And resuming from just below the collision -- a cursor at the 255th append's position --
            // must still yield BOTH entries that share the 256th append's position, proving the
            // collision handling holds at the start of a copy too, not only at its enforced end.
            List<StateChangeEnvelope> afterBoundary = await DrainAsync(
                store, new StateChangeCursor(appended[254].Record!.GlobalPosition));
            Assert.Equal(2, afterBoundary.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<List<StateChangeEnvelope>> DrainAsync(
        IStateChangeFeed feed,
        StateChangeCursor? from,
        StateChangeReadOptions? options = null)
    {
        List<StateChangeEnvelope> changes = [];
        await foreach (StateChangeEnvelope envelope in feed.ReadAsync(from, options ?? StateChangeReadOptions.Default))
        {
            changes.Add(envelope);
        }

        return changes;
    }

    private static StateCommit Commit(string value) => new()
    {
        Operation = StateOperation.Set,
        Status = StateStatus.Ready,
        ValueType = typeof(string).FullName!,
        SchemaVersion = 1,
        Payload = Encoding.UTF8.GetBytes(value),
        Source = "test",
    };
}
