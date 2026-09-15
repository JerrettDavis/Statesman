namespace Statesman;

public interface IStateSerializer
{
    byte[] Serialize<T>(T value);

    byte[] Serialize(object value, Type type);

    T? Deserialize<T>(ReadOnlySpan<byte> payload);

    object? Deserialize(ReadOnlySpan<byte> payload, Type type);

    /// <summary>
    /// This serializer's stable identity, stamped into <see cref="StateEnvelope.SerializerId"/> on
    /// every record the runtime writes through it.
    /// </summary>
    /// <remarks>
    /// A default interface member so that an existing implementation keeps compiling and keeps
    /// working: the default answers with the implementing type's name, which identifies the encoding
    /// as well as anything did before envelopes existed. Override it with a stable string, and treat
    /// that string as a persisted contract once records carry it, exactly as
    /// <c>JsonStateSerializer</c> does.
    /// </remarks>
    string SerializerId => GetType().FullName ?? GetType().Name;

    /// <summary>
    /// The media type of the bytes this serializer produces, stamped into
    /// <see cref="StateEnvelope.ContentType"/>.
    /// </summary>
    /// <remarks>
    /// The default is <c>application/octet-stream</c>, which is the honest answer for a serializer
    /// that has not said otherwise rather than a guess at JSON.
    /// </remarks>
    string ContentType => "application/octet-stream";
}
