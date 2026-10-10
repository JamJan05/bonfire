using Bonfire.Core;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Servers;

/// <summary>
/// Random id of this Bonfire installation, stored once. Docker resources carry it as a label so
/// that two installations (or a test run) on one daemon never touch each other's containers.
/// </summary>
public static class InstallationIdentity
{
    public static async Task<Guid> LoadOrCreateAsync(AppPaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var file = Path.Combine(paths.DataDir, "installation-id");

        if (File.Exists(file))
        {
            var text = (await File.ReadAllTextAsync(file, cancellationToken)).Trim();
            return Guid.TryParseExact(text, "N", out var existing)
                ? existing
                : throw new InvalidDataException($"Installation id file {file} is corrupt.");
        }

        var id = Guid.NewGuid();
        await AtomicFile.WriteAllTextAsync(file, id.ToString("N"), cancellationToken);
        return id;
    }
}
