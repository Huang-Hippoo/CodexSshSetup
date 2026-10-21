using CodexSshSetup.App.Services;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.Core.Tests;

public sealed class TunnelSupervisorTests
{
    private static readonly ServerProfile Profile = new("host", "user", 22, "node02");

    [Fact]
    public async Task Duplicate_connect_reuses_the_owned_tunnel()
    {
        var factory = new FakeTunnelProcessFactory();
        await using var supervisor = new TunnelSupervisor(factory, ImmediateDelay);

        await supervisor.ConnectAsync(Profile, 7890);
        await supervisor.ConnectAsync(Profile, 7890);

        Assert.Equal(1, factory.StartCount);
        await supervisor.DisconnectAsync();
    }

    [Fact]
    public async Task Explicit_disconnect_kills_only_the_process_started_by_this_supervisor()
    {
        var factory = new FakeTunnelProcessFactory();
        var unrelated = new FakeTunnelProcess();
        await using var supervisor = new TunnelSupervisor(factory, ImmediateDelay);
        await supervisor.ConnectAsync(Profile, 7890);

        await supervisor.DisconnectAsync();

        Assert.True(factory.Sessions.Single().WasKilled);
        Assert.False(unrelated.WasKilled);
        Assert.Equal(TunnelState.Disconnected, supervisor.State);
    }

    [Fact]
    public async Task Unexpected_exit_starts_a_replacement_tunnel()
    {
        var factory = new FakeTunnelProcessFactory();
        await using var supervisor = new TunnelSupervisor(factory, ImmediateDelay);
        await supervisor.ConnectAsync(Profile, 7890);

        factory.Sessions[0].Exit(255);
        await WaitUntilAsync(() => factory.StartCount >= 2);

        Assert.True(factory.StartCount >= 2);
        await supervisor.DisconnectAsync();
    }

    [Fact]
    public async Task Initial_process_exit_is_reported_instead_of_claiming_connected()
    {
        var factory = new FakeTunnelProcessFactory { ExitFirstImmediately = true };
        await using var supervisor = new TunnelSupervisor(factory, ImmediateDelay);

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.ConnectAsync(Profile, 7890));

        Assert.Equal(TunnelState.Failed, supervisor.State);
    }

    [Fact]
    public async Task Running_process_is_not_connected_until_remote_confirms_readiness()
    {
        var factory = new FakeTunnelProcessFactory { HoldReady = true };
        await using var supervisor = new TunnelSupervisor(factory, ImmediateDelay);
        var connecting = supervisor.ConnectAsync(Profile, 7890);

        Assert.False(connecting.IsCompleted);
        Assert.Equal(TunnelState.Connecting, supervisor.State);
        factory.Sessions.Single().ConfirmReady();
        await connecting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(TunnelState.Connected, supervisor.State);
    }

    [Fact]
    public async Task Disconnect_during_startup_cancels_pending_connect_and_owned_process()
    {
        var factory = new FakeTunnelProcessFactory { HoldReady = true };
        await using var supervisor = new TunnelSupervisor(factory, ImmediateDelay);
        var connecting = supervisor.ConnectAsync(Profile, 7890);

        await supervisor.DisconnectAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        Assert.True(factory.Sessions.Single().WasKilled);
    }

    private static Task ImmediateDelay(TimeSpan _, CancellationToken cancellationToken) =>
        Task.Delay(1, cancellationToken);

    [Fact]
    public async Task Two_nodes_use_same_remote_port_and_disconnect_or_reconnect_independently()
    {
        var firstFactory = new FakeTunnelProcessFactory();
        var secondFactory = new FakeTunnelProcessFactory();
        await using var first = new TunnelSupervisor(firstFactory, ImmediateDelay);
        await using var second = new TunnelSupervisor(secondFactory, ImmediateDelay);
        await Task.WhenAll(first.ConnectAsync(Profile with { Alias = "node01" }, 7890),
            second.ConnectAsync(Profile with { Alias = "node03", Host = "other-host" }, 7890));

        Assert.Equal(TunnelState.Connected, first.State);
        Assert.Equal(TunnelState.Connected, second.State);
        firstFactory.Sessions[0].Exit(255);
        await WaitUntilAsync(() => firstFactory.StartCount >= 2 && first.State == TunnelState.Connected);
        Assert.Equal(1, secondFactory.StartCount);
        Assert.False(secondFactory.Sessions[0].WasKilled);

        await first.DisconnectAsync();
        Assert.Equal(TunnelState.Connected, second.State);
        Assert.False(secondFactory.Sessions[0].WasKilled);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    private sealed class FakeTunnelProcessFactory : ITunnelProcessFactory
    {
        public List<FakeTunnelProcess> Sessions { get; } = [];
        public int StartCount => Sessions.Count;
        public bool ExitFirstImmediately { get; init; }
        public bool HoldReady { get; init; }

        public ITunnelProcess Start(CommandSpec command)
        {
            var process = new FakeTunnelProcess();
            Sessions.Add(process);
            if (ExitFirstImmediately && Sessions.Count == 1) process.Exit(255);
            else if (!HoldReady) process.ConfirmReady();
            return process;
        }
    }

    private sealed class FakeTunnelProcess : ITunnelProcess
    {
        private readonly TaskCompletionSource<int> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => _exit.Task.IsCompleted;
        public bool WasKilled { get; private set; }
        public int? ExitCode => _exit.Task.IsCompletedSuccessfully ? _exit.Task.Result : null;
        public Task WaitForReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);
        public void ConfirmReady() => _ready.TrySetResult();

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            WasKilled = true;
            _exit.TrySetResult(-1);
        }

        public void Exit(int code)
        {
            _exit.TrySetResult(code);
            _ready.TrySetException(new InvalidOperationException("Tunnel exited before ready."));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
