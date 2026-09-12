using Wolpertinger.AppHost.Activation;
using Wolpertinger.AppHost.Lifecycle;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Tests;

public sealed class ProductInstanceGuardTests
{
    [Fact]
    public async Task SecondGuardForSameProductForwardsOneActivationToPrimaryPipe()
    {
        var productId = $"WOLPERTINGER.TEST.{Guid.NewGuid():N}";
        using var first = ProductInstanceGuard.Acquire(productId);
        using var second = ProductInstanceGuard.Acquire(productId);
        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
        Assert.Equal(first.ActivationPipeName, second.ActivationPipeName);

        var channel = new ActivationChannel(first.ActivationPipeName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = channel.ReceiveOneAsync(timeout.Token);
        var expected = new ProductActivation(ProductActivationKind.Open);
        await second.ForwardAsync(expected, timeout.Token);
        Assert.Equal(expected, await receive);
    }

    [Fact]
    public void DifferentProductIdsDoNotSharePrimaryOwnership()
    {
        using var first = ProductInstanceGuard.Acquire($"A.{Guid.NewGuid():N}");
        using var second = ProductInstanceGuard.Acquire($"B.{Guid.NewGuid():N}");
        Assert.True(first.IsPrimary);
        Assert.True(second.IsPrimary);
        Assert.NotEqual(first.ActivationPipeName, second.ActivationPipeName);
    }
}
