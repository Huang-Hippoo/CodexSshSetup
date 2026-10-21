using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.App.Infrastructure;

public sealed class ConnectionProcessRunner(IProcessRunner inner, Func<ServerProfile> profile) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(CommandSpec command, IProgress<string>? progress, CancellationToken token) =>
        inner.RunAsync(SshCommandFactory.WithConnection(command, profile()), progress, token);
}
