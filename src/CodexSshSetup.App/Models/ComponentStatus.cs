namespace CodexSshSetup.App.Models;

public enum ComponentState
{
    Unknown,
    Checking,
    Ready,
    Warning,
    Failed
}

public sealed record ComponentStatus(
    string Name,
    ComponentState State,
    string Summary,
    string? Detail = null);

public sealed record RemoteToolInfo(
    string NodeVersion,
    string NpmVersion,
    string CodexPath,
    string CodexVersion,
    bool ManagedWrapperExists)
{
    public bool HasNode20OrNewer => Core.Parsing.ToolVersion.IsNode20OrNewer(NodeVersion);
    public bool HasNpm => !string.IsNullOrWhiteSpace(NpmVersion) && NpmVersion != "missing";
    public bool HasCodex => !string.IsNullOrWhiteSpace(CodexPath) && CodexPath != "missing";
    public bool IsReadyForDesktop => HasCodex && ManagedWrapperExists;
}
