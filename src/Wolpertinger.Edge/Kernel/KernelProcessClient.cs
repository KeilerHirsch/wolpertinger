using System.Buffers.Binary;
using System.Diagnostics;
using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Kernel;

internal interface IKernelProcessClient : IAsyncDisposable
{
    bool IsHealthy { get; }
    int? ProcessId { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task SetRoleAsync(ulong epoch, KernelRole role, CancellationToken cancellationToken = default);
    Task<KernelApplyResult> ApplyAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public sealed record KernelApplyResult(
    KernelResponseStatus Status,
    ulong Epoch,
    KernelRole Role,
    ObservationCursor? Cursor,
    FixedBytes32 StateDigest,
    KernelJumpFact? JumpFact,
    KernelCommanderVesselFact? CommanderVesselFact = null);

public sealed class KernelProcessClient : IKernelProcessClient
{
    private readonly KernelProcessOptions _options;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private bool _unhealthy;
    private bool _disposed;

    public KernelProcessClient(KernelProcessOptions options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    public bool IsHealthy => !_disposed && !_unhealthy && _process is { HasExited: false };
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("Kernel process client is already started.");
        }

        var startInfo = new ProcessStartInfo(_options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_options.ExecutablePath)!,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start trusted kernel process.");
        }
        try
        {
            _options.ProcessContainment?.Assign(process);
            cancellationToken.ThrowIfCancellationRequested();
            _process = process;
            _ = process.StandardError.ReadToEndAsync();
            await Task.CompletedTask;
        }
        catch
        {
            _process = null;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            process.Dispose();
            throw;
        }
    }

    public async Task SetRoleAsync(
        ulong epoch,
        KernelRole role,
        CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            CborContractCodec.EncodeSetRole(epoch, role),
            cancellationToken).ConfigureAwait(false);

        if (response.Kind != KernelResponseKind.Role
            || response.Status != KernelResponseStatus.Ok
            || response.Epoch != epoch
            || response.Role != role)
        {
            MarkUnhealthy();
            throw new InvalidDataException("Trusted kernel returned an invalid SetRole response.");
        }
    }

    public async Task<KernelApplyResult> ApplyAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var response = await ExchangeAsync(
            CborContractCodec.Encode(observation),
            cancellationToken).ConfigureAwait(false);
        if (response.Kind != KernelResponseKind.Apply)
        {
            MarkUnhealthy();
            throw new InvalidDataException("Trusted kernel returned a non-Apply response.");
        }

        return new KernelApplyResult(
            response.Status,
            response.Epoch,
            response.Role,
            response.Cursor,
            response.StateDigest,
            response.JumpFact,
            response.CommanderVesselFact);
    }

    private async Task<KernelResponse> ExchangeAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var process = RequireHealthyProcess();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        var ct = timeout.Token;

        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            process = RequireHealthyProcess();
            await WriteFrameAsync(process.StandardInput.BaseStream, payload, ct).ConfigureAwait(false);
            var responsePayload = await ReadFrameAsync(process.StandardOutput.BaseStream, ct).ConfigureAwait(false);
            return CborContractCodec.DecodeKernelResponse(responsePayload);
        }
        catch
        {
            MarkUnhealthy();
            throw;
        }
        finally
        {
            _requestGate.Release();
        }
    }
    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > KernelProtocol.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Trusted kernel request payload length is invalid.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > KernelProtocol.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Trusted kernel response frame length is invalid.");
        }

        var payload = new byte[(int)length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("Trusted kernel closed the protocol channel mid-frame.");
            }
            read += count;
        }
    }

    private Process RequireHealthyProcess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_unhealthy)
        {
            throw new InvalidOperationException("Trusted kernel client is unhealthy.");
        }
        if (_process is null || _process.HasExited)
        {
            _unhealthy = true;
            throw new InvalidOperationException("Trusted kernel process is not running.");
        }
        return _process;
    }

    private void MarkUnhealthy()
        => _unhealthy = true;
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            process.Dispose();
            _process = null;
            _unhealthy = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _requestGate.Dispose();
    }
}
