using Wolpertinger.Presentation.App.Onboarding;

namespace Wolpertinger.Presentation.Tests.Onboarding;

public sealed class FirstRunStateStoreTests
{
    [Fact]
    public async Task MissingOrCorruptStateMeansNotCompleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wol-first-run-{Guid.NewGuid():N}.json");
        var store = new FirstRunStateStore(path);

        Assert.False((await store.LoadAsync()).Completed);

        await File.WriteAllTextAsync(path, "{ nope");
        Assert.False((await store.LoadAsync()).Completed);

        File.Delete(path);
    }

    [Fact]
    public async Task CompletedStateRoundTripsWithoutAccountData()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wol-first-run-{Guid.NewGuid():N}.json");
        var store = new FirstRunStateStore(path);
        try
        {
            await store.SaveAsync(new FirstRunState(
                FirstRunState.CurrentSchemaVersion,
                Completed: true));

            var loaded = await store.LoadAsync();
            Assert.True(loaded.Completed);
            var serialized = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("commander", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
