using System.Buffers.Binary;
using System.Text.Json;

namespace Wolpertinger.Product.Contracts;

public static class ProductActivationCodec
{
    public const int MaximumFrameBytes = 8_192;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async ValueTask WriteAsync(
        Stream stream,
        ProductActivation activation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ProductActivationValidator.Validate(activation);

        var payload = JsonSerializer.SerializeToUtf8Bytes(activation, JsonOptions);
        if (payload.Length is 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Activation frame exceeds maximum size.");

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ProductActivation> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException("Activation frame length is invalid.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        ProductActivation activation;
        try
        {
            activation = JsonSerializer.Deserialize<ProductActivation>(payload, JsonOptions)
                ?? throw new InvalidDataException("Activation payload is missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Activation payload is malformed.", ex);
        }

        ProductActivationValidator.Validate(activation);
        return activation;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("Activation frame is truncated.");
            offset += read;
        }
    }
}
