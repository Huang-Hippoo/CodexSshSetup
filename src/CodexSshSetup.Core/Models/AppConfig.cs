namespace CodexSshSetup.Core.Models;

public enum CloseBehavior
{
    Ask,
    HideAndKeepTunnel,
    StopTunnelAndExit
}

public sealed record AppConfig
{
    public ServerProfile? Profile { get; init; }
    public int? LocalProxyPort { get; init; }
    public CloseBehavior CloseBehavior { get; init; } = CloseBehavior.Ask;
}
