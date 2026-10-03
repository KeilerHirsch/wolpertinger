namespace Wolpertinger.Presentation.App.Onboarding;

public sealed record FirstRunState(
    int SchemaVersion,
    bool Completed)
{
    public const int CurrentSchemaVersion = 1;
    public static FirstRunState Default { get; } =
        new(CurrentSchemaVersion, Completed: false);
}
