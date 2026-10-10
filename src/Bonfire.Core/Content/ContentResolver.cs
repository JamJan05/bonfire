namespace Bonfire.Core.Content;

public enum PlanProblemKind
{
    AlreadyInstalled,
    NoCompatibleVersion,
    VersionIncompatible,
    NoInstallableFile,
    UnresolvedDependency,
    InstalledVersionConflict,
    VersionConflict,
    DependencyDisabled,
    Incompatible,
    NameCollision,
    LimitExceeded,
}

public enum PlanWarningKind
{
    Prerelease,
    ClientOnly,
    EnvironmentUnknown,
    UnverifiedFilesPresent,
}

public sealed record PlanProblem(PlanProblemKind Kind, string? Subject = null, string? Other = null);

public sealed record PlanWarning(PlanWarningKind Kind, string Subject);

/// <summary>An optional dependency of a planned version (with its exact version, if declared).</summary>
public sealed record OptionalDependency(string ProjectId, string? VersionId);

/// <summary>One file to download and install.</summary>
public sealed record PlannedItem(string ProjectTitle, ModrinthVersion Version, ModrinthFile File, string InstallName, bool IsDependency);

/// <summary>What installing a project means: files, warnings to confirm, problems that block it.</summary>
public sealed record InstallPlan(
    IReadOnlyList<PlannedItem> Items,
    IReadOnlyList<PlanWarning> Warnings,
    IReadOnlyList<PlanProblem> Problems,
    IReadOnlyList<OptionalDependency> OptionalDependencies,
    string Fingerprint,
    IReadOnlyList<InstalledItem>? Replaces = null)
{
    /// <summary>Installed files this plan removes (the old versions of updated add-ons).</summary>
    public IReadOnlyList<InstalledItem> ReplacedItems => Replaces ?? [];

    public bool CanInstall => Problems.Count == 0 && Items.Count > 0;

    public long TotalBytes => Items.Sum(i => i.File.Size);
}

/// <summary>
/// Plans installing a Modrinth project with its required dependencies. Exact version constraints
/// win over "newest"; a constraint found after an unconstrained choice restarts resolution with it
/// seeded. This is a bounded heuristic, not a complete solver: when its limits are hit it says so.
/// </summary>
public sealed class ContentResolver
{
    public const int MaxProjects = 50;
    public const int MaxApiCalls = 200;
    public const int MaxRestarts = 5;
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    private readonly IModrinth _api;
    private readonly ContentInventory _inventory;
    private readonly IReadOnlyList<InstalledItem> _replacing;
    private int _calls;

    /// <param name="replacing">Installed files that the plan replaces: they do not count as installed.</param>
    public ContentResolver(IModrinth api, ContentInventory inventory, IReadOnlyList<InstalledItem>? replacing = null)
    {
        _api = api;
        _inventory = inventory;
        _replacing = replacing ?? [];
    }

    /// <summary>Active files that stay: the proposed final inventory minus what is planned.</summary>
    private IEnumerable<InstalledItem> Active => _inventory.Active.Where(i => !_replacing.Contains(i));

    private ContentProfile Profile => _inventory.Profile;

    public Task<InstallPlan> PlanAsync(string projectId, string? versionId, CancellationToken cancellationToken) =>
        PlanAsync([(projectId, versionId)], cancellationToken);

    /// <summary>Plans several roots at once (an update: each root with the exact version to update to).</summary>
    public async Task<InstallPlan> PlanAsync(IReadOnlyList<(string Project, string? Version)> roots, CancellationToken cancellationToken)
    {
        // Seeded exact constraints: project → (version, the version that demanded it; null = the user).
        var seeds = new Dictionary<string, (string Version, string? Source)>(StringComparer.Ordinal);
        foreach (var (project, version) in roots.Where(r => r.Version is not null))
        {
            seeds[project] = (version!, null);
        }

        for (var attempt = 0; attempt <= MaxRestarts; attempt++)
        {
            Attempt result;
            try
            {
                result = await AttemptAsync(roots.Select(r => r.Project).Distinct().ToList(), seeds, cancellationToken);
            }
            catch (ResolutionLimitException)
            {
                return Fail(new PlanProblem(PlanProblemKind.LimitExceeded));
            }

            if (result.Restart is { } restart)
            {
                seeds[restart.Project] = (restart.Version, restart.Source);
                continue;
            }

            var plan = result.Plan!;
            // A seed demanded by a version that is no longer part of the plan is stale: give up honestly.
            var chosen = plan.Items.Select(i => i.Version.Id).ToHashSet(StringComparer.Ordinal);
            if (plan.Problems.Count == 0 && seeds.Values.Any(s => s.Source is not null && !chosen.Contains(s.Source)))
            {
                return Fail(new PlanProblem(PlanProblemKind.LimitExceeded));
            }

            return plan;
        }

        return Fail(new PlanProblem(PlanProblemKind.LimitExceeded));
    }

    private InstallPlan Fail(PlanProblem problem) => new([], [], [problem], [], _inventory.Fingerprint, _replacing);

    private sealed record Attempt(InstallPlan? Plan, (string Project, string Version, string Source)? Restart);

    private sealed record Pending(string ProjectId, string? Version, string? Source, bool IsRoot);

    private async Task<Attempt> AttemptAsync(IReadOnlyList<string> roots, Dictionary<string, (string Version, string? Source)> seeds, CancellationToken ct)
    {
        var problems = new List<PlanProblem>();
        var warnings = new List<PlanWarning>();
        var optional = new List<OptionalDependency>();
        var chosen = new Dictionary<string, (ModrinthVersion Version, ModrinthFile File, string Title, bool Constrained, bool IsDependency)>(StringComparer.Ordinal);
        var order = new List<string>();
        var queue = new Queue<Pending>();
        foreach (var root in roots)
        {
            queue.Enqueue(new Pending(root, seeds.TryGetValue(root, out var rs) ? rs.Version : null, null, true));
        }

        while (queue.Count > 0)
        {
            var (project, constraint, source, isRoot) = queue.Dequeue();
            if (constraint is null && seeds.TryGetValue(project, out var seed))
            {
                constraint = seed.Version;
            }

            // Installed already?
            var active = Active.FirstOrDefault(i => i.ProjectId == project);
            if (active is not null)
            {
                if (isRoot && constraint is null)
                {
                    problems.Add(new PlanProblem(PlanProblemKind.AlreadyInstalled, active.Title));
                }
                else if (constraint is not null && active.VersionId != constraint)
                {
                    problems.Add(new PlanProblem(PlanProblemKind.InstalledVersionConflict, active.Title, constraint));
                }
                else if (isRoot)
                {
                    problems.Add(new PlanProblem(PlanProblemKind.AlreadyInstalled, active.Title));
                }

                continue;
            }

            if (_inventory.Items.FirstOrDefault(i => i.Disabled && i.ProjectId == project) is { } disabled)
            {
                // A disabled copy never satisfies a dependency, and a second copy would collide with it.
                problems.Add(new PlanProblem(PlanProblemKind.DependencyDisabled, disabled.Title));
                continue;
            }

            if (chosen.TryGetValue(project, out var existing))
            {
                if (constraint is not null && existing.Version.Id != constraint)
                {
                    if (existing.Constrained)
                    {
                        problems.Add(new PlanProblem(PlanProblemKind.VersionConflict, existing.Title, constraint));
                    }
                    else
                    {
                        return new Attempt(null, (project, constraint, source!));
                    }
                }

                continue;
            }

            if (chosen.Count >= MaxProjects)
            {
                return new Attempt(Fail(new PlanProblem(PlanProblemKind.LimitExceeded)), null);
            }

            ModrinthVersion? version;
            if (constraint is not null)
            {
                version = await Call(() => _api.GetVersionAsync(constraint, ct));
                if (version.ProjectId != project || !IsCompatible(version))
                {
                    problems.Add(new PlanProblem(PlanProblemKind.VersionIncompatible, project, constraint));
                    continue;
                }
            }
            else
            {
                var versions = await Call(() => _api.GetCompatibleVersionsAsync(project, Profile, ct));
                version = versions.FirstOrDefault(v => v.IsRelease) ?? versions.FirstOrDefault();
                if (version is null)
                {
                    problems.Add(new PlanProblem(PlanProblemKind.NoCompatibleVersion, project));
                    continue;
                }
            }

            var info = await Call(() => _api.GetProjectAsync(project, ct));
            var title = string.IsNullOrWhiteSpace(info.Title) ? project : info.Title;
            if (ContentRules.SelectFile(version) is not { } file)
            {
                problems.Add(new PlanProblem(PlanProblemKind.NoInstallableFile, title));
                continue;
            }

            chosen[project] = (version, file, title, constraint is not null, !isRoot);
            order.Add(project);

            if (!version.IsRelease)
            {
                warnings.Add(new PlanWarning(PlanWarningKind.Prerelease, title));
            }

            switch (ContentRules.Environment(version.Environment, info.ServerSide))
            {
                case EnvironmentSupport.ClientOnly:
                    warnings.Add(new PlanWarning(PlanWarningKind.ClientOnly, title));
                    break;
                case EnvironmentSupport.Unknown:
                    warnings.Add(new PlanWarning(PlanWarningKind.EnvironmentUnknown, title));
                    break;
            }

            foreach (var dep in version.Dependencies)
            {
                switch (dep.Type)
                {
                    case DependencyType.Required:
                        var depProject = dep.ProjectId;
                        if (depProject is null && dep.VersionId is not null)
                        {
                            depProject = (await Call(() => _api.GetVersionAsync(dep.VersionId, ct))).ProjectId;
                        }

                        if (depProject is null)
                        {
                            problems.Add(new PlanProblem(PlanProblemKind.UnresolvedDependency, title, dep.FileName));
                        }
                        else
                        {
                            queue.Enqueue(new Pending(depProject, dep.VersionId, version.Id, false));
                        }

                        break;
                    case DependencyType.Optional when dep.ProjectId is not null:
                        optional.Add(new OptionalDependency(dep.ProjectId, dep.VersionId));
                        break;
                }
            }
        }

        // Incompatibilities in both directions: planned → installed/planned, installed → planned.
        var activeKnown = Active.Where(i => i.IsKnown).ToList();

        // Files that stay keep their exact requirements: a planned different version breaks them.
        foreach (var item in activeKnown)
        {
            foreach (var dep in item.Dependencies.Where(d => d.Type == DependencyType.Required && d.VersionId is not null))
            {
                // A version-only requirement names its project through the file that has that version (perhaps one being replaced).
                var project = dep.ProjectId ?? _inventory.Items.FirstOrDefault(i => i.VersionId == dep.VersionId)?.ProjectId;
                if (project is not null && chosen.TryGetValue(project, out var hit) && hit.Version.Id != dep.VersionId)
                {
                    problems.Add(new PlanProblem(PlanProblemKind.InstalledVersionConflict, item.Title, hit.Title));
                }
            }
        }
        foreach (var (project, c) in chosen)
        {
            foreach (var dep in c.Version.Dependencies.Where(d => d.Type == DependencyType.Incompatible))
            {
                var named = new LockDependency(dep.ProjectId, dep.VersionId, dep.Type);
                foreach (var other in chosen.Values.Where(o => named.Names(o.Version.ProjectId, o.Version.Id)))
                {
                    problems.Add(new PlanProblem(PlanProblemKind.Incompatible, c.Title, other.Title));
                }

                foreach (var hit in activeKnown.Where(i => named.Names(i.ProjectId, i.VersionId)))
                {
                    problems.Add(new PlanProblem(PlanProblemKind.Incompatible, c.Title, hit.Title));
                }
            }

            foreach (var item in activeKnown)
            {
                if (item.Dependencies.Any(d => d.Type == DependencyType.Incompatible && d.Names(project, c.Version.Id)))
                {
                    problems.Add(new PlanProblem(PlanProblemKind.Incompatible, item.Title, c.Title));
                }
            }
        }

        // File names: never overwrite; fall back to an id-based name.
        var items = new List<PlannedItem>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in order)
        {
            var c = chosen[project];
            var name = ContentRules.InstallFileName(c.File.FileName, project, c.Version.Id);
            if (_inventory.NameTaken(name) || taken.Contains(name))
            {
                name = ContentRules.InstallFileName("", project, c.Version.Id);
            }

            if (_inventory.NameTaken(name) || !taken.Add(name))
            {
                problems.Add(new PlanProblem(PlanProblemKind.NameCollision, c.Title, name));
                continue;
            }

            items.Add(new PlannedItem(c.Title, c.Version, c.File, name, c.IsDependency));
        }

        if (items.Sum(i => i.File.Size) > MaxTotalBytes)
        {
            problems.Add(new PlanProblem(PlanProblemKind.LimitExceeded));
        }

        if (Active.Any(i => !i.IsKnown))
        {
            warnings.Add(new PlanWarning(PlanWarningKind.UnverifiedFilesPresent, ""));
        }

        var optionalMissing = optional.Distinct()
            .Where(o => !chosen.ContainsKey(o.ProjectId) && Active.All(i => i.ProjectId != o.ProjectId))
            .ToList();
        return new Attempt(new InstallPlan(items, warnings, problems.Distinct().ToList(), optionalMissing, _inventory.Fingerprint, _replacing), null);
    }

    public bool IsCompatible(ModrinthVersion version) =>
        version.GameVersions.Contains(Profile.GameVersion) && version.Loaders.Any(Profile.Loaders.Contains);

    private async Task<T> Call<T>(Func<Task<T>> call)
    {
        if (++_calls > MaxApiCalls)
        {
            throw new ResolutionLimitException();
        }

        return await call();
    }
}

public sealed class ResolutionLimitException() : Exception("Resolution limit exceeded");
