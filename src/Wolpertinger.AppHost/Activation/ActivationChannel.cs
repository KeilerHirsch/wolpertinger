using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Activation;

public sealed class ActivationChannel
{
    public const int MaximumPayloadBytes = 8_192;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _pipeName;

    public ActivationChannel(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public async Task<ProductActivation> ReceiveOneAsync(
        CancellationToken cancellationToken = default)
    {
        await using var server = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadFrameAsync(server, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(
        ProductActivation activation,
        CancellationToken cancellationToken = default)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await WriteFrameAsync(client, activation, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteFrameAsync(
        Stream stream,
        ProductActivation activation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(activation);
        if (!Enum.IsDefined(activation.Kind))
            throw new InvalidDataException("Activation kind is undefined.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(activation, JsonOptions);
        if (payload.Length is 0 or > MaximumPayloadBytes)
            throw new InvalidDataException("Activation frame exceeds maximum size.");

        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ProductActivation> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaximumPayloadBytes)
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
        if (!Enum.IsDefined(activation.Kind))
            throw new InvalidDataException("Activation kind is undefined.");
        return activation;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidDataException("Activation frame is truncated.");
            offset += read;
        }
    }
}
