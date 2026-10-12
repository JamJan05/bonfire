using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.Services;

/// <summary>Everything the user decided, frozen at the moment "Create" was clicked.</summary>
public sealed record CreationRequest(
    string DisplayName,
    ResolvedServerVersion Version,
    ResourceLimits Resources,
    int Port,
    PortExposure Exposure,
    RestartPolicyKind RestartPolicy,
    GameSettings Game,
    DateTimeOffset EulaAcceptedAt,
    bool StartAfterCreate,
    string? HostAddress = null);

public enum CreationPhase
{
    Validating,
    PullingImage,
    CreatingServer,
    Starting,
    Done,
}

public sealed record CreationProgress(CreationPhase Phase, ImagePullProgress? Pull = null);

/// <summary>The server exists; it may or may not have started.</summary>
/// <param name="StartCancelled">Starting was cancelled: whether Docker started the container is unknown.</param>
public sealed record CreationResult(Guid ServerId, bool Started, ServerOperationException? StartError, bool StartCancelled = false);

public sealed class CreationValidationException(IReadOnlyList<string> errors)
    : Exception($"Invalid server: {string.Join(", ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Thrown when creation was interrupted after the server record was written.</summary>
public sealed class CreationInterruptedException(Guid serverId, Exception inner)
    : Exception("Creation interrupted after the record was written", inner)
{
    public Guid ServerId { get; } = serverId;
}

/// <summary>
/// Creates a server: validate everything first, then pull the image and pin its digest, then
/// write the record and provision. Starting is a separate step — a start failure leaves a valid,
/// created server.
/// </summary>
public sealed class ServerCreationService
{
    private const string PlaceholderDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly ServerOperationsService _operations;
    private readonly IPortAvailability _ports;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerCreationService> _logger;

    public ServerCreationService(
        ServerRegistry registry,
        IServerRuntime runtime,
        ServerOperationsService operations,
        IPortAvailability ports,
        TimeProvider time,
        ILogger<ServerCreationService> logger)
    {
        _registry = registry;
        _runtime = runtime;
        _operations = operations;
        _ports = ports;
        _time = time;
        _logger = logger;
    }

    public async Task<CreationResult> CreateAsync(CreationRequest request, IProgress<CreationProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report(new CreationProgress(CreationPhase.Validating));

        _operations.RequireSafeDaemon();
        var definition = BuildDefinition(request, new ImageReference(ImageReference.ItzgRepository, $"java{request.Version.JavaMajor}", PlaceholderDigest));
        ThrowIfInvalid(definition);

        var port = await _ports.CheckAsync(PortRules.HostIp(definition), request.Port, PortCheckScope.All, cancellationToken);
        if (port is PortCheck.UsedByBonfireServer or PortCheck.UsedByContainer or PortCheck.UsedByHost)
        {
            throw new ServerOperationException(ServerOperationError.PortInUse, port.ToString());
        }

        // Nothing persistent so far (pulled image layers are shared and stay cached).
        progress?.Report(new CreationProgress(CreationPhase.PullingImage));
        var image = await _runtime.AcquireImageAsync(
            definition.Image.Tag,
            new Progress<ImagePullProgress>(p => progress?.Report(new CreationProgress(CreationPhase.PullingImage, p))),
            cancellationToken);

        definition = definition with { Image = image.Reference };
        ThrowIfInvalid(definition);
        _operations.RequireSafeDaemon(); // the daemon may have changed during the download

        progress?.Report(new CreationProgress(CreationPhase.CreatingServer));
        var record = ServerRecord.New(definition);
        await _registry.CreateAsync(record, CancellationToken.None);
        try
        {
            await _runtime.ProvisionAsync(record, r => _registry.SaveAsync(r, CancellationToken.None), cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ServerOperationException)
        {
            // The record stays in phase Provisioning: the list offers Repair.
            _logger.LogWarning(ex, "Creating server {ServerId} was interrupted", definition.Id);
            throw new CreationInterruptedException(definition.Id, ex);
        }

        if (!request.StartAfterCreate)
        {
            progress?.Report(new CreationProgress(CreationPhase.Done));
            return new CreationResult(definition.Id, false, null);
        }

        progress?.Report(new CreationProgress(CreationPhase.Starting));
        try
        {
            await _operations.StartAsync(definition.Id, cancellationToken);
        }
        catch (ServerOperationException ex)
        {
            progress?.Report(new CreationProgress(CreationPhase.Done));
            return new CreationResult(definition.Id, false, ex);
        }
        catch (OperationCanceledException)
        {
            // The server already exists; cancelling only stopped waiting for the start.
            return new CreationResult(definition.Id, false, null, StartCancelled: true);
        }

        progress?.Report(new CreationProgress(CreationPhase.Done));
        return new CreationResult(definition.Id, true, null);
    }

    public ServerDefinition BuildDefinition(CreationRequest request, ImageReference image)
    {
        var v = request.Version;
        return new ServerDefinition(
            Id: Guid.NewGuid(),
            DisplayName: request.DisplayName,
            Type: v.Type,
            MinecraftVersion: v.MinecraftVersion,
            FabricLoaderVersion: v.FabricLoaderVersion,
            FabricLauncherVersion: v.FabricLauncherVersion,
            PaperBuild: v.PaperBuild,
            PaperExperimentalBuild: false, // only stable Paper builds are offered
            JavaMajor: v.JavaMajor,
            Image: image,
            Resources: request.Resources,
            Port: request.Port,
            Exposure: request.Exposure,
            RestartPolicy: request.RestartPolicy,
            Game: request.Game,
            EulaAcceptedAt: request.EulaAcceptedAt,
            CreatedAt: _time.GetUtcNow(),
            HostAddress: request.HostAddress);
    }

    /// <summary>Validation errors for a request, using a placeholder digest (the real one comes from the pull).</summary>
    public IReadOnlyList<string> Validate(CreationRequest request) =>
        ServerDefinitionValidator.Validate(BuildDefinition(
            request, new ImageReference(ImageReference.ItzgRepository, $"java{request.Version.JavaMajor}", PlaceholderDigest)));

    private static void ThrowIfInvalid(ServerDefinition definition)
    {
        var errors = ServerDefinitionValidator.Validate(definition);
        if (errors.Count > 0)
        {
            throw new CreationValidationException(errors);
        }
    }
}
