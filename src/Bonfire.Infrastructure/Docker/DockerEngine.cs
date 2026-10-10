using Bonfire.Core.Docker;
using Docker.DotNet;
using Docker.DotNet.Models;
using Docker.DotNet.Unix;
using Microsoft.Extensions.Logging;

namespace Bonfire.Infrastructure.Docker;

/// <summary>Docker Engine access through Docker.DotNet.Enhanced over a local Unix socket.</summary>
public sealed class DockerEngine : IDockerEngine
{
    public static readonly TimeSpan DefaultProbeDeadline = TimeSpan.FromSeconds(10);

    private readonly DockerEndpoint? _endpoint;
    private readonly TimeProvider _time;
    private readonly ILogger<DockerEngine> _logger;
    private readonly TimeSpan _probeDeadline;

    public DockerEngine(DockerEndpoint? endpoint, TimeProvider time, ILogger<DockerEngine> logger, TimeSpan? probeDeadline = null)
    {
        _endpoint = endpoint;
        _time = time;
        _logger = logger;
        _probeDeadline = probeDeadline ?? DefaultProbeDeadline;
    }

    public async Task<DockerProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (_endpoint is null)
        {
            return new DockerProbeResult(DockerConnectionStatus.UnsupportedEndpoint, null, _time.GetUtcNow());
        }

        // The deadline covers the whole probe; only the caller's token surfaces as cancellation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_probeDeadline);

        // Unversioned client: /version must be readable before we know which API version to use.
        using var client = CreateClient(_endpoint, apiVersion: null);

        VersionResponse version;
        try
        {
            version = await client.System.GetVersionAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(DockerConnectionStatus.Timeout, detail: $"No response within {_probeDeadline.TotalSeconds:0} s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var status = DockerErrorClassifier.Classify(ex);
            var description = DockerErrorClassifier.Describe(ex);
            // Expected conditions (no access, daemon stopped) are not worth a stack trace.
            _logger.LogWarning("Docker probe of {Endpoint} failed: {Status} ({Detail})", _endpoint, status, description);
            _logger.LogDebug(ex, "Docker probe exception");
            return Result(status, detail: description);
        }

        if (!System.Version.TryParse(version.APIVersion, out var maxApi)
            || !System.Version.TryParse(version.MinAPIVersion, out var minApi))
        {
            return Result(DockerConnectionStatus.Failed, detail: "Daemon did not report a valid API version range");
        }

        var negotiated = DockerApiVersion.Negotiate(minApi, maxApi);
        var daemon = new DockerDaemonInfo(
            ServerVersion: version.Version ?? "?",
            MinimumApiVersion: minApi,
            MaximumApiVersion: maxApi,
            NegotiatedApiVersion: negotiated,
            OperatingSystem: version.Os,
            KernelVersion: version.KernelVersion,
            Architecture: version.Arch);

        if (negotiated is null)
        {
            return Result(
                DockerConnectionStatus.ApiIncompatible,
                daemon,
                detail: $"Daemon API {minApi}–{maxApi}, Bonfire {DockerApiVersion.ClientMinimum}–{DockerApiVersion.ClientMaximum}");
        }

        // /info only enriches the result; failing to read it must not turn into "feature absent".
        DockerCapabilities? capabilities = null;
        string? detail = null;
        try
        {
            using var versioned = CreateClient(_endpoint, negotiated);
            var info = await versioned.System.GetSystemInfoAsync(deadline.Token);
            capabilities = SecurityOptions.ToCapabilities(info.SecurityOptions, info.CgroupVersion, info.CgroupDriver);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            detail = "/info timed out";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            detail = "/info: " + DockerErrorClassifier.Describe(ex);
            _logger.LogWarning("Reading Docker /info failed: {Detail}", detail);
            _logger.LogDebug(ex, "Docker /info exception");
        }

        _logger.LogInformation(
            "Connected to Docker {Version} at {Endpoint}, API {Api}", daemon.ServerVersion, _endpoint, negotiated);
        return Result(DockerConnectionStatus.Connected, daemon, capabilities, detail);
    }

    private DockerProbeResult Result(
        DockerConnectionStatus status,
        DockerDaemonInfo? daemon = null,
        DockerCapabilities? capabilities = null,
        string? detail = null) =>
        new(status, _endpoint, _time.GetUtcNow(), daemon, capabilities, detail);

    private static DockerClient CreateClient(DockerEndpoint endpoint, System.Version? apiVersion)
    {
        // The endpoint is always explicit, so DOCKER_HOST/Docker contexts can never redirect us.
        var builder = new DockerClientBuilder().WithEndpoint(endpoint.Uri);
        if (apiVersion is not null)
        {
            builder = builder.WithApiVersion(apiVersion);
        }

        return builder.WithTransportOptions(new UnixSocketTransportOptions()).Build();
    }
}
