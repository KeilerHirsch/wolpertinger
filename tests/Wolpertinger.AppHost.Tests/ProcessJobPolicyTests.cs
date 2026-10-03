using System.Diagnostics;
using Wolpertinger.AppHost.Windows;
using Wolpertinger.Edge.Kernel;

namespace Wolpertinger.AppHost.Tests;

public sealed partial class ProcessJobPolicyTests
{
    [Fact]
    public void EdgeContainmentSeamIsExactlyAssignProcess()
    {
        var method = Assert.Single(typeof(IChildProcessContainment).GetMethods());
        Assert.Equal("Assign", method.Name);
        Assert.Equal(typeof(void), method.ReturnType);
        Assert.Equal(new[] { typeof(Process) }, method.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void JobPolicyFreezesKillOnCloseFlag()
    {
        Assert.Equal(0x00002000u, ProcessJob.KillOnJobCloseLimit);
    }
}

public sealed partial class ProcessJobPolicyTests
{
    [Fact]
    public async Task KernelClientAssignsStartedProcessToContainment()
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var containment = new RecordingContainment();
        var options = new KernelProcessOptions(
            executable,
            TimeSpan.FromSeconds(2),
            containment);

        await using var client = new KernelProcessClient(options);
        await client.StartAsync();

        Assert.NotNull(containment.Process);
        Assert.Equal(client.ProcessId, containment.Process!.Id);
        Assert.False(containment.Process.HasExited);
    }

    private sealed class RecordingContainment : IChildProcessContainment
    {
        public Process? Process { get; private set; }
        public int? ProcessId { get; private set; }
        public void Assign(Process process)
        {
            Process = process;
            ProcessId = process.Id;
        }
    }
}

public sealed partial class ProcessJobPolicyTests
{
    [Fact]
    public async Task CancelledKernelStartDoesNotLeaveContainedProcessRunning()
    {
        var executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        var containment = new RecordingContainment();
        var options = new KernelProcessOptions(executable, TimeSpan.FromSeconds(2), containment);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using var client = new KernelProcessClient(options);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.StartAsync(cancellation.Token));
            var processId = Assert.IsType<int>(containment.ProcessId);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        }
        finally
        {
            if (containment.ProcessId is int processId)
            {
                try
                {
                    using var leftover = Process.GetProcessById(processId);
                    leftover.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
            }
        }
    }
}
