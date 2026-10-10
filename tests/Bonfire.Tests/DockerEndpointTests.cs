using Bonfire.Core.Docker;
using Bonfire.Infrastructure.Docker;

namespace Bonfire.Tests;

public sealed class DockerEndpointTests
{
    private static DockerEndpoint? Resolve(string? dockerHost) =>
        DockerEndpoint.Resolve(key => key == "DOCKER_HOST" ? dockerHost : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DefaultsToSystemSocket(string? value)
    {
        Assert.Equal("/var/run/docker.sock", Resolve(value)!.SocketPath);
    }

    [Fact]
    public void AcceptsAbsoluteUnixSocket()
    {
        var endpoint = Resolve("unix:///run/user/1000/docker.sock");

        Assert.Equal("/run/user/1000/docker.sock", endpoint!.SocketPath);
        Assert.Equal("unix:///run/user/1000/docker.sock", endpoint.ToString());
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:2375")]
    [InlineData("ssh://user:secret@host")]
    [InlineData("npipe:////./pipe/docker_engine")]
    [InlineData("unix://relative/docker.sock")]
    [InlineData("unix://")]
    [InlineData("unix:////host/docker.sock")]
    [InlineData("unix:///run/docker.sock?x=1")]
    [InlineData("unix:///run/docker.sock#frag")]
    [InlineData("unix:///run/docker%2esock")]
    [InlineData("unix:///run/do\0cker.sock")]
    [InlineData("/var/run/docker.sock")]
    public void RejectsAnythingButALocalAbsoluteSocket(string value)
    {
        Assert.Null(Resolve(value));
    }

    [Theory]
    [InlineData("1.40", "1.56", "1.56")]
    [InlineData("1.24", "1.44", "1.44")]
    [InlineData("1.44", "1.99", "1.56")]
    public void NegotiatesHighestCommonVersion(string min, string max, string expected)
    {
        Assert.Equal(Version.Parse(expected), DockerApiVersion.Negotiate(Version.Parse(min), Version.Parse(max)));
    }

    [Theory]
    [InlineData("1.24", "1.43")]
    [InlineData("1.57", "1.60")]
    public void NoOverlapMeansNoVersion(string min, string max)
    {
        Assert.Null(DockerApiVersion.Negotiate(Version.Parse(min), Version.Parse(max)));
    }

    [Fact]
    public void SecurityOptionsAreParsedByName()
    {
        var caps = SecurityOptions.ToCapabilities(
            ["name=seccomp,profile=unconfined", "name=cgroupns", "name=rootless"], "2", "systemd");

        Assert.Equal(true, caps.Seccomp);
        Assert.Equal("unconfined", caps.SeccompProfile);
        Assert.Equal(true, caps.Rootless);
        Assert.Equal("systemd", caps.CgroupDriver);
    }

    [Fact]
    public void MissingSecurityOptionsAreUnknownNotFalse()
    {
        var caps = SecurityOptions.ToCapabilities(null, null, null);

        Assert.Null(caps.Seccomp);
        Assert.Null(caps.Rootless);
    }
}
