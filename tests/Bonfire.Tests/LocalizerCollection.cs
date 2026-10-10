namespace Bonfire.Tests;

/// <summary>Tests touching the process-wide <c>Localizer.Instance</c> must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizerCollection
{
    public const string Name = "Localizer";
}
