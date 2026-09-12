using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.Tests.Contracts;

public sealed class PresentationFrameCodecTests
{
    [Fact]
    public async Task AcceptsExactMaximumFrameAndReadsOnlyOneFrame()
    {
        const string json = "{\"protocolVersion\":2,\"revision\":0,\"context\":\"InactiveOrNoGame\",\"intent\":{\"composition\":\"Quiet\",\"overlayEmphasized\":false,\"reasonCode\":\"Startup\",\"selectionMode\":\"Auto\"},\"jump\":null,\"commanderVessel\":null,\"runtimeHealth\":{\"health\":\"Starting\",\"reasonCode\":\"Startup\"},\"frontierAccount\":{\"state\":\"Disconnected\",\"freshness\":\"Unknown\",\"lastSuccessUnixMs\":null,\"reasonCode\":\"NotConnected\"}}";
        var payload = Encoding.UTF8.GetBytes(json.PadRight(PresentationProtocol.MaximumFrameBytes));
        await using var stream = new MemoryStream();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
        await PresentationFrameCodec.WriteAsync(stream, TestSnapshots.Jump(2));
        stream.Position = 0;
        Assert.Equal(PresentationSnapshot.Empty, await PresentationFrameCodec.ReadAsync(stream));
        Assert.Equal(4 + PresentationProtocol.MaximumFrameBytes, stream.Position);
        Assert.Equal(TestSnapshots.Jump(2), await PresentationFrameCodec.ReadAsync(stream));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripAndWireFormat(bool empty)
    {
        var snapshot = empty ? PresentationSnapshot.Empty : TestSnapshots.Jump(1);
        await using var stream = new MemoryStream();
        await PresentationFrameCodec.WriteAsync(stream, snapshot);
        var bytes = stream.ToArray();
        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32BigEndian(bytes));
        var json = Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4);
        Assert.Contains("\"protocolVersion\":2", json);
        if (!empty) Assert.Contains("\"LocalJournal\"", json);
        stream.Position = 0;
        Assert.Equal(snapshot, await PresentationFrameCodec.ReadAsync(stream));
        await using var partial = new PartialStream(bytes);
        Assert.Equal(snapshot, await PresentationFrameCodec.ReadAsync(partial));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    [InlineData(int.MaxValue)]
    public async Task RejectsLengthBeforeReadingPayload(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, length);
        await using var stream = new PrefixOnlyStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PresentationFrameCodec.ReadAsync(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DistinguishesCleanEofFromTruncation(int count)
    {
        byte[] bytes = [0, 0, 0, 2, 123, 125];
        await using var stream = new PartialStream(bytes[..count]);
        if (count == 0)
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await PresentationFrameCodec.ReadAsync(stream));
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await PresentationFrameCodec.ReadAsync(stream));
            Assert.Equal("Truncated presentation frame.", error.Message);
        }
    }

    [Theory]
    [InlineData("{\"protocolVersion\":1", true)]
    [InlineData("{} {}", true)]
    [InlineData("null", false)]
    [InlineData("{\"protocolVersion\":2,\"revision\":0,\"jump\":null}", false)]
    public async Task RejectsInvalidSnapshot(string json, bool malformed)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        stream.Write(prefix);
        stream.Write(payload);
        stream.Position = 0;
        if (malformed)
            await Assert.ThrowsAsync<JsonException>(async () => await PresentationFrameCodec.ReadAsync(stream));
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => await PresentationFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task RejectsOversizedWriteBeforeWritingPrefix()
    {
        var snapshot = TestSnapshots.Jump(1);
        snapshot = snapshot with { Jump = snapshot.Jump! with { StarSystem = new string('x', 65536) } };
        await using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PresentationFrameCodec.WriteAsync(stream, snapshot));
        Assert.Equal(0, stream.Length);
    }

    private sealed class PartialStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }

    private sealed class PrefixOnlyStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.True(Position < 4, "Invalid length must be rejected before requesting payload.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
