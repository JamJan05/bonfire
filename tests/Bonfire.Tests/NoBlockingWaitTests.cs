namespace Bonfire.Tests;

/// <summary>
/// Blocking on a task (GetAwaiter().GetResult(), .Wait(), .Result) on the UI thread deadlocks as soon
/// as the awaited code continues on that thread — it froze the app while enabling a mod. Only the
/// listed places, which never run under the UI's synchronization context, may do it.
/// </summary>
public sealed class NoBlockingWaitTests
{
    private static readonly string[] Allowed =
    [
        "src/Bonfire.App/Startup.cs",                        // before the UI exists
        "src/Bonfire.App/App.axaml.cs",                      // exit: the flushed work runs on the thread pool
        "src/Bonfire.App/ViewModels/ServerBackupsViewModel.cs", // inside a continuation of an already completed task
    ];

    [Fact]
    public void SourceHasNoNewBlockingWaits()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal))
            .Where(f => !Allowed.Contains(Path.GetRelativePath(root, f)))
            .Where(f => File.ReadAllText(f) is var text
                        && (text.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal) || text.Contains(".Wait(", StringComparison.Ordinal)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.Empty(offenders);
    }
}
