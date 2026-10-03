namespace Wolpertinger.AppHost.Preferences;

public sealed record ProductPreferences(
    int SchemaVersion,
    string? EliteDataDirectory)
{
    public const int CurrentSchemaVersion = 1;
    public static ProductPreferences Default { get; } =
        new(CurrentSchemaVersion, EliteDataDirectory: null);
}
