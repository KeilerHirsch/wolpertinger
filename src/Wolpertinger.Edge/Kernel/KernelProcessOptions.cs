namespace Wolpertinger.Edge.Kernel;

public sealed record KernelProcessOptions
{
    public KernelProcessOptions(
        string executablePath,
        TimeSpan requestTimeout,
        IChildProcessContainment? processContainment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (requestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));

        ExecutablePath = executablePath;
        RequestTimeout = requestTimeout;
        ProcessContainment = processContainment;
    }

    public string ExecutablePath { get; }
    public TimeSpan RequestTimeout { get; }
    public IChildProcessContainment? ProcessContainment { get; }
}
