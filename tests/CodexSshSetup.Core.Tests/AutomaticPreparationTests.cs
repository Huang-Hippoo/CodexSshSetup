using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.App.Services;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Proxy;

namespace CodexSshSetup.Core.Tests;

public sealed class AutomaticPreparationTests
{
    private static readonly ServerProfile Profile = new("host", "user", 22, "node");
    private const string Ready = "NODE=v20.17.0\nNPM=10\nCODEX_PATH=/home/user/.local/bin/codex\nCODEX_VERSION=codex-cli 1.0\nWRAPPER=yes\n";
    private const string Missing = "NODE=v20.17.0\nNPM=10\nCODEX_PATH=missing\nCODEX_VERSION=missing\nWRAPPER=no\n";

    [Fact]
    public async Task Detects_different_computer_port_when_previously_saved_port_is_unavailable()
    {
        var service = new ProxyDetectionService((port, _) => Task.FromResult(new ProxyProbeResult(port == 7897, port == 7897 ? 200 : null, "probe")));
        var result = await service.DetectAsync(7890, CancellationToken.None);
        Assert.True(result.ConnectSucceeded);
        Assert.Equal(7897, result.Port);
        var tunnel = SshCommandFactory.CreateTunnel(Profile, result.Port!.Value);
        Assert.Contains("127.0.0.1:17890:127.0.0.1:7897", tunnel.Arguments);
    }

    [Fact]
    public void Reads_custom_local_ports_from_system_proxy_format_without_using_external_hosts()
    {
        Assert.Equal(new[] { 43123, 45678 }, ProxyDetectionService.ParseLoopbackPorts("http=127.0.0.1:43123;https=localhost:45678"));
        Assert.Empty(ProxyDetectionService.ParseLoopbackPorts("http://external.example:7890"));
    }

    [Fact]
    public async Task Existing_ready_codex_is_not_reinstalled_or_upgraded()
    {
        var runner = new QueueRunner(new ProcessResult(0, Ready, ""));
        var service = new RemotePreparationService(runner, new SshProvisioningService(runner, new FileLog()));
        var tools = await service.EnsureCodexAsync(Profile, null, CancellationToken.None);
        Assert.True(tools.IsReadyForDesktop);
        Assert.Single(runner.Commands);
        Assert.Null(runner.Commands[0].StandardInput);
    }

    [Fact]
    public async Task Missing_codex_is_installed_then_verified_and_existing_node_is_reused()
    {
        var runner = new QueueRunner(new ProcessResult(0, Missing, ""), new ProcessResult(0, Missing, ""),
            new ProcessResult(0, "CODEX_INSTALL=downloading\ncodex-cli 1.0", ""), new ProcessResult(0, Ready, ""));
        var service = new RemotePreparationService(runner, new SshProvisioningService(runner, new FileLog()));
        Assert.True((await service.EnsureCodexAsync(Profile, null, CancellationToken.None)).IsReadyForDesktop);
        Assert.Equal(4, runner.Commands.Count);
        Assert.Single(runner.Commands.Where(command => command.StandardInput is not null));
        Assert.Contains("@openai/codex", runner.Commands[2].StandardInput);
        Assert.DoesNotContain(runner.Commands, command => command.Arguments.Contains("command -v conda"));
    }

    [Fact]
    public async Task Failed_install_is_reported_without_claiming_ready()
    {
        var runner = new QueueRunner(new ProcessResult(0, Missing, ""), new ProcessResult(0, Missing, ""), new ProcessResult(1, "", "registry unreachable"));
        var service = new RemotePreparationService(runner, new SshProvisioningService(runner, new FileLog()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureCodexAsync(Profile, null, CancellationToken.None));
        Assert.Contains("registry unreachable", error.Message);
    }

    [Fact]
    public async Task Existing_different_user_proxy_is_reported_as_preserved()
    {
        var runner = new QueueRunner(new ProcessResult(0, "PROXY_CONFIG=existing_different", ""));
        var service = new RemotePreparationService(runner, new SshProvisioningService(runner, new FileLog()));
        Assert.False(await service.EnsureUserProxyAsync(Profile, null, CancellationToken.None));
    }

    private sealed class QueueRunner(params ProcessResult[] responses) : IProcessRunner
    {
        private readonly Queue<ProcessResult> _responses = new(responses);
        public List<CommandSpec> Commands { get; } = [];
        public Task<ProcessResult> RunAsync(CommandSpec command, IProgress<string>? progress, CancellationToken token)
        {
            Commands.Add(command);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
