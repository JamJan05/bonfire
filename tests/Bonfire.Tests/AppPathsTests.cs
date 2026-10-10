using Bonfire.Core;

namespace Bonfire.Tests;

public sealed class AppPathsTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void UsesFallbacksUnderHomeWhenXdgVariablesAreUnset()
    {
        var paths = AppPaths.Resolve(Env(("HOME", "/home/u")));

        Assert.Equal("/home/u/.config/bonfire", paths.ConfigDir);
        Assert.Equal("/home/u/.local/share/bonfire", paths.DataDir);
        Assert.Equal("/home/u/.local/state/bonfire", paths.StateDir);
        Assert.Equal("/home/u/.cache/bonfire", paths.CacheDir);
        Assert.Equal("/home/u/.local/state/bonfire/logs", paths.LogDir);
    }

    [Fact]
    public void UsesAbsoluteXdgVariables()
    {
        var paths = AppPaths.Resolve(Env(
            ("HOME", "/home/u"),
            ("XDG_CONFIG_HOME", "/cfg"),
            ("XDG_DATA_HOME", "/data"),
            ("XDG_STATE_HOME", "/state"),
            ("XDG_CACHE_HOME", "/cache")));

        Assert.Equal("/cfg/bonfire", paths.ConfigDir);
        Assert.Equal("/data/bonfire", paths.DataDir);
        Assert.Equal("/state/bonfire", paths.StateDir);
        Assert.Equal("/cache/bonfire", paths.CacheDir);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/dir")]
    [InlineData("./dir")]
    public void IgnoresEmptyAndRelativeXdgValues(string value)
    {
        var paths = AppPaths.Resolve(Env(("HOME", "/home/u"), ("XDG_DATA_HOME", value)));

        Assert.Equal("/home/u/.local/share/bonfire", paths.DataDir);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    public void RejectsMissingOrRelativeHome(string? home)
    {
        var env = home is null ? Env() : Env(("HOME", home));

        Assert.Throws<InvalidOperationException>(() => AppPaths.Resolve(env));
    }

    [Fact]
    public void LockFileIsInStateDirectory()
    {
        var paths = AppPaths.Resolve(Env(("HOME", "/home/u"), ("XDG_STATE_HOME", "/state")));

        Assert.Equal("/state/bonfire/instance.lock", paths.InstanceLockFile);
    }
}
