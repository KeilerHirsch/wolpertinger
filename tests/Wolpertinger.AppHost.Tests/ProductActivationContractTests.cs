using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Tests;

public sealed class ProductActivationContractTests
{
    [Fact]
    public void PayloadIsRejectedForKindsThatDoNotOwnPayload()
        => Assert.Throws<InvalidDataException>(() =>
            ProductActivationValidator.Validate(
                new ProductActivation(ProductActivationKind.EnterSample, "unexpected")));

    [Theory]
    [InlineData("Flight")]
    [InlineData("JumpPreparation")]
    [InlineData("Station")]
    public void ManualCompositionUsesBoundedSymbolicContract(string value)
        => ProductActivationValidator.Validate(
            new ProductActivation(ProductActivationKind.SetManualComposition, value));

    [Fact]
    public void ProtocolCallbackIsBoundToRegisteredApplicationUri()
    {
        ProductActivationValidator.Validate(new ProductActivation(
            ProductActivationKind.ProtocolCallback,
            "wolpertinger://frontier/oauth?code=abc&state=xyz"));

        Assert.Throws<InvalidDataException>(() =>
            ProductActivationValidator.Validate(new ProductActivation(
                ProductActivationKind.ProtocolCallback,
                "https://attacker.invalid/oauth?code=abc&state=xyz")));
    }

    [Fact]
    public void UnknownManualCompositionIsRejected()
        => Assert.Throws<InvalidDataException>(() =>
            ProductActivationValidator.Validate(
                new ProductActivation(ProductActivationKind.SetManualComposition, "Everything")));

    [Fact]
    public void ElitePathMustBeOneExistingAbsoluteDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wolpertinger-elite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            ProductActivationValidator.Validate(
                new ProductActivation(ProductActivationKind.SetEliteDataPath, path));
            Assert.Throws<InvalidDataException>(() =>
                ProductActivationValidator.Validate(
                    new ProductActivation(ProductActivationKind.SetEliteDataPath, "relative")));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public async Task SharedCodecRoundTripsValidatedActivation()
    {
        await using var stream = new MemoryStream();
        var value = new ProductActivation(
            ProductActivationKind.SetManualComposition,
            "GalaxyMap");

        await ProductActivationCodec.WriteAsync(stream, value);
        stream.Position = 0;
        var roundTrip = await ProductActivationCodec.ReadAsync(stream);

        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void EndpointNamingIsStableForCurrentUser()
    {
        var first = ProductActivationEndpoint.PipeName();
        var second = ProductActivationEndpoint.PipeName();

        Assert.Equal(first, second);
        Assert.StartsWith("WOLPERTINGER.", first, StringComparison.Ordinal);
        Assert.EndsWith(".activation", first, StringComparison.Ordinal);
    }
}
