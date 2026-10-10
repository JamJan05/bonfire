namespace Bonfire.Infrastructure.Linux;

/// <summary>The real user and group the application runs as; containers run with the same ids.</summary>
public static class HostUser
{
    public static int Uid => checked((int)LibC.GetUid());

    public static int Gid => checked((int)LibC.GetGid());
}
