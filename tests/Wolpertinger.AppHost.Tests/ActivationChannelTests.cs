using System.Buffers.Binary;
using System.Text;
using Wolpertinger.AppHost.Activation;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Tests;

public sealed class ActivationChannelTests
{
    [Fact]
    public void ActivationKindsAreFrozen()
    {
        Assert.Equal(new byte[] { 1,2,3,4,5,6,7,8,9,10,11 },
            Enum.GetValues<ProductActivationKind>().Select(value => (byte)value));
    }

    [Fact]
    public async Task FrameRoundTripsWithBigEndianLengthPrefix()
    {
        var activation = new ProductActivation(ProductActivationKind.SetEliteDataPath, @"C:\Elite");
        await using var stream = new MemoryStream();
        await ActivationChannel.WriteFrameAsync(stream, activation);
        var bytes = stream.ToArray();
        Assert.InRange(bytes.Length, 5, ActivationChannel.MaximumPayloadBytes + 4);
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(bytes));
        stream.Position = 0;
        Assert.Equal(activation, await ActivationChannel.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task OversizedWriteFailsBeforeTouchingStream()
    {
        var activation = new ProductActivation(ProductActivationKind.ProtocolCallback,
            new string('x', ActivationChannel.MaximumPayloadBytes));
        await using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ActivationChannel.WriteFrameAsync(stream, activation));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task InvalidLengthFailsBeforePayloadRead()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, ActivationChannel.MaximumPayloadBytes + 1);
        await using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ActivationChannel.ReadFrameAsync(stream));
    }
}
