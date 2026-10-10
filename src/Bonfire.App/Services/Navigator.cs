namespace Bonfire.App.Services;

/// <summary>Switches the main content between the pages that are not in the sidebar.</summary>
public sealed class Navigator
{
    public event EventHandler? NewServerRequested;

    public event EventHandler? ServersRequested;

    public event EventHandler<Guid>? ServerDetailsRequested;

    public void ShowServer(Guid serverId) => ServerDetailsRequested?.Invoke(this, serverId);

    public void ShowNewServer() => NewServerRequested?.Invoke(this, EventArgs.Empty);

    public void ShowServers() => ServersRequested?.Invoke(this, EventArgs.Empty);
}
