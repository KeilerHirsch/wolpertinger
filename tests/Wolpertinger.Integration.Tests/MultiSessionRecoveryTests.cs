using System.Text;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Runtime;

namespace Wolpertinger.Integration.Tests;

public sealed class MultiSessionRecoveryTests
{
    [Fact]
    public async Task NewJournalSessionRebindsAndRestartReplaysBothSessions()
    {
        var root = FsdJumpVerticalSliceTests.RepoRoot();
        var data = FsdJumpVerticalSliceTests.TempData();
        var kernel = FsdJumpVerticalSliceTests.Kernel(root);

        await using (var runner = await VerticalSliceRunner.OpenAsync(data, kernel))
        {
            await SessionAsync(runner, "2026-10-04T00:00:00Z", "F100", "One", 1);
            await SessionAsync(runner, "2026-10-04T01:00:00Z", "F100", "Two", 2);

            Assert.Equal(new ObservationCursor(4, 0), runner.KernelDiagnostics.LastAgreedCursor);
            Assert.Equal(2, runner.Outputs.Count);
            Assert.Equal("Two", runner.Outputs[^1].StarSystem);
        }

        await using var restarted = await VerticalSliceRunner.OpenAsync(data, kernel);
        Assert.Equal(new ObservationCursor(4, 0), restarted.KernelDiagnostics.LastAgreedCursor);
        Assert.NotNull(restarted.FinalStateDigest);

        await restarted.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(
            """{"timestamp":"2026-10-04T01:00:03Z","event":"FSDJump","StarSystem":"After Restart","SystemAddress":3,"StarPos":[3,4,5],"JumpDist":3,"FuelUsed":1,"FuelLevel":17}"""));

        Assert.Equal(new ObservationCursor(5, 0), restarted.KernelDiagnostics.LastAgreedCursor);
        Assert.Equal("After Restart", Assert.Single(restarted.Outputs).StarSystem);
    }

    private static async Task SessionAsync(
        VerticalSliceRunner runner,
        string timestamp,
        string fid,
        string system,
        ulong address)
    {
        await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(
            $"{{\"timestamp\":\"{timestamp}\",\"event\":\"Fileheader\",\"part\":1,\"gameversion\":\"4.2.2.0\",\"build\":\"r300000/r0\"}}"));
        await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(
            $"{{\"timestamp\":\"{timestamp}\",\"event\":\"Commander\",\"FID\":\"{fid}\"}}"));
        await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(
            $"{{\"timestamp\":\"{timestamp}\",\"event\":\"FSDJump\",\"StarSystem\":\"{system}\",\"SystemAddress\":{address},\"StarPos\":[1,2,3],\"JumpDist\":1,\"FuelUsed\":1,\"FuelLevel\":18}}"));
    }
}
