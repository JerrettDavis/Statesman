using System.Text.Json;
using Statesman.Testing;

namespace Statesman.Tests;

public sealed class SerializerEnvelopeTests
{
    private static readonly StateKey<int> Counter = StateKey.Define<int>("envelope/counter");

    private static StatesmanDeclaration Declaration() =>
        global::Statesman.Statesman.Declare("envelopes")
            .State(Counter, state => state.Initial(0))
            .Build();

    [Fact]
    public async Task An_append_carries_an_envelope_naming_the_serializer_the_runtime_writes_through()
    {
        StatesmanDeclaration declaration = Declaration();
        var clock = new ManualTimeProvider();
        var store = new InMemoryStateLedgerStore("memory", clock);
        await using StatesmanTestHarness harness =
            StatesmanTestHarness.Create(declaration, stores: [store], time: clock);

        await harness.Runtime.State(Counter).SetAsync(7, cancellationToken: TestContext.Current.CancellationToken);

        StateRecord? record = await store.ReadLatestAsync(
            new StateAddress("envelopes", Counter.Path, StatePartition.Default),
            TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        StateEnvelope? envelope = record.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal(JsonStateSerializer.MediaType, envelope.ContentType);
        Assert.Equal(JsonStateSerializer.Id, envelope.SerializerId);
        Assert.Equal(declaration.Manifest.Fingerprint, envelope.Fingerprint);
        Assert.Equal(StateEnvelope.CurrentFormatVersion, envelope.FormatVersion);
    }

    [Fact]
    public async Task A_cleared_record_carries_no_envelope_because_it_carries_no_payload()
    {
        // The null-payload arm of the runtime's stamping helper. An envelope describes bytes, so a
        // record with none must not claim a content type, and this is the row that says so.
        StatesmanDeclaration declaration = Declaration();
        var clock = new ManualTimeProvider();
        var store = new InMemoryStateLedgerStore("memory", clock);
        await using StatesmanTestHarness harness =
            StatesmanTestHarness.Create(declaration, stores: [store], time: clock);
        var address = new StateAddress("envelopes", Counter.Path, StatePartition.Default);

        await harness.Runtime.State(Counter).SetAsync(7, cancellationToken: TestContext.Current.CancellationToken);
        await harness.Runtime.State(Counter).ClearAsync(cancellationToken: TestContext.Current.CancellationToken);

        StateRecord? record = await store.ReadLatestAsync(address, TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        Assert.Null(record.Payload);
        Assert.Null(record.Envelope);
    }

    [Fact]
    public async Task A_serializer_written_before_envelopes_existed_still_compiles_and_still_identifies_itself()
    {
        // ProbeSerializer implements the four methods IStateSerializer has always declared and
        // nothing else, which is exactly the shape a consumer's own serializer has. It compiles
        // because SerializerId and ContentType are default interface members; deleting those two
        // bodies is this task's compile-level lever, and it turns this fixture into CS0535.
        IStateSerializer serializer = new ProbeSerializer();
        Assert.Equal(typeof(ProbeSerializer).FullName, serializer.SerializerId);
        Assert.Equal("application/octet-stream", serializer.ContentType);

        StatesmanDeclaration declaration = Declaration();
        var clock = new ManualTimeProvider();
        var store = new InMemoryStateLedgerStore("memory", clock);
        await using var resolver = new StateStoreResolver([store], ownsStores: false);
        await using IStatesman runtime =
            declaration.CreateRuntime(new TestServiceProvider().Add<TimeProvider>(clock), resolver, serializer, clock);

        await runtime.State(Counter).SetAsync(7, cancellationToken: TestContext.Current.CancellationToken);

        StateRecord? record = await store.ReadLatestAsync(
            new StateAddress("envelopes", Counter.Path, StatePartition.Default),
            TestContext.Current.CancellationToken);
        Assert.NotNull(record);
        StateEnvelope? envelope = record.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal(typeof(ProbeSerializer).FullName, envelope.SerializerId);
        Assert.Equal("application/octet-stream", envelope.ContentType);
    }

    private sealed class ProbeSerializer : IStateSerializer
    {
        public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

        public byte[] Serialize(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type);

        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => JsonSerializer.Deserialize<T>(payload);

        public object? Deserialize(ReadOnlySpan<byte> payload, Type type) =>
            JsonSerializer.Deserialize(payload, type);
    }
}
