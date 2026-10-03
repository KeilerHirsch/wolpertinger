using Wolpertinger.AppHost.Activation;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Lifecycle;

public sealed class ProductInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private ProductInstanceGuard(Mutex mutex, bool isPrimary, string activationPipeName)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        ActivationPipeName = activationPipeName;
    }

    public bool IsPrimary { get; }
    public string ActivationPipeName { get; }

    public static ProductInstanceGuard Acquire(string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        var mutexName = ProductActivationEndpoint.MutexName(productId);
        var pipeName = ProductActivationEndpoint.PipeName(productId);
        var mutex = new Mutex(initiallyOwned: false, mutexName, out var createdNew);
        return new ProductInstanceGuard(mutex, createdNew, pipeName);
    }

    public Task ForwardAsync(
        ProductActivation activation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ActivationChannel(ActivationPipeName).SendAsync(activation, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _mutex.Dispose();
    }
}
