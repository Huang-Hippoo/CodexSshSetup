using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.App.Services;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.Core.Tests;

public sealed class RemoteServiceTimeoutTests
{
    private static readonly ServerProfile Profile =
        new("192.0.2.10", "researcher", 22, "node02");

    [Fact]
    public async Task Tool_inspection_allows_slow_remote_codex_startup()
    {
        var runner = new RecordingRunner(new ProcessResult(0,
            "NODE=v22.0.0\nNPM=10.0.0\nCODEX_PATH=/home/user/.local/bin/codex\nCODEX_VERSION=codex-cli 0.1.0\nWRAPPER=yes\n",
            ""));
        var service = new SshProvisioningService(runner, new FileLog());

        await service.InspectToolsAsync(Profile, null, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(120), runner.Command!.Timeout);
    }

    [Fact]
    public async Task Login_status_allows_slow_remote_codex_startup()
    {
        var runner = new RecordingRunner(new ProcessResult(0, "", "Logged in using ChatGPT"));
        var service = new CodexRemoteService(runner);

        var status = await service.GetLoginStatusAsync(Profile, null, CancellationToken.None);

        Assert.True(status.LoggedIn);
        Assert.Equal(TimeSpan.FromSeconds(60), runner.Command!.Timeout);
    }

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public CommandSpec? Command { get; private set; }

        public Task<ProcessResult> RunAsync(
            CommandSpec command, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Command = command;
            return Task.FromResult(result);
        }
    }
}
