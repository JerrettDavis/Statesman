namespace Statesman;

public interface IStateSerializer
{
    byte[] Serialize<T>(T value);

    byte[] Serialize(object value, Type type);

    T? Deserialize<T>(ReadOnlySpan<byte> payload);

    object? Deserialize(ReadOnlySpan<byte> payload, Type type);
}
