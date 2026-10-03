using System.IO.Pipes;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Activation;

public sealed class ActivationChannel
{
    public const int MaximumPayloadBytes = ProductActivationCodec.MaximumFrameBytes;
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
        return await ProductActivationCodec.ReadAsync(
            server, cancellationToken).ConfigureAwait(false);
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
        await ProductActivationCodec.WriteAsync(
            client, activation, cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask WriteFrameAsync(
        Stream stream,
        ProductActivation activation,
        CancellationToken cancellationToken = default)
        => ProductActivationCodec.WriteAsync(stream, activation, cancellationToken);

    public static ValueTask<ProductActivation> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
        => ProductActivationCodec.ReadAsync(stream, cancellationToken);
}
