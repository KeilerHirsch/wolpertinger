using System.IO.Pipes;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.Presentation.App.Activation;

public interface IProductActivationClient
{
    Task SendAsync(
        ProductActivation activation,
        CancellationToken cancellationToken = default);
}

public sealed class ProductActivationClient : IProductActivationClient
{
    private readonly string _pipeName;

    public ProductActivationClient(
        string pipeName = "")
        => _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? ProductActivationEndpoint.PipeName()
            : pipeName;

    public async Task SendAsync(
        ProductActivation activation,
        CancellationToken cancellationToken = default)
    {
        ProductActivationValidator.Validate(activation);
        await using var client = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await ProductActivationCodec.WriteAsync(
            client,
            activation,
            cancellationToken).ConfigureAwait(false);
    }
}
