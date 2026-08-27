namespace CodexSshSetup.Core.Commands;

public sealed record CommandSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StandardInput = null,
    bool Interactive = false,
    TimeSpan? Timeout = null);
