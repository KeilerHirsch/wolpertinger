using System.Diagnostics;
using Wolpertinger.Edge.Kernel;

namespace Wolpertinger.AppHost.Lifecycle;

public interface IPresentationProcessLauncher
{
    IPresentationProcessHandle Launch();
}

public interface IPresentationProcessHandle : IAsyncDisposable
{
    Task<TimeSpan> WaitForExitAsync(CancellationToken cancellationToken);
}

public sealed class PresentationProcessLauncher : IPresentationProcessLauncher
{
    private readonly string _executablePath;
    private readonly string _pipeName;
    private readonly IChildProcessContainment _containment;

    public PresentationProcessLauncher(
        string executablePath,
        string pipeName,
        IChildProcessContainment containment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _executablePath = Path.GetFullPath(executablePath);
        _pipeName = pipeName;
        _containment = containment ?? throw new ArgumentNullException(nameof(containment));
    }
    public IPresentationProcessHandle Launch()
    {
        var startInfo = BuildStartInfo(_executablePath, _pipeName);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start Presentation process.");
        }

        try
        {
            _containment.Assign(process);
            return new PresentationProcessHandle(process);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }

    private static ProcessStartInfo BuildStartInfo(string executablePath, string pipeName)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        return startInfo;
    }

    private sealed class PresentationProcessHandle : IPresentationProcessHandle
    {
        private readonly Process _process;
        private readonly Stopwatch _lifetime = Stopwatch.StartNew();
        public PresentationProcessHandle(Process process)
            => _process = process;

        public async Task<TimeSpan> WaitForExitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return _lifetime.Elapsed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
