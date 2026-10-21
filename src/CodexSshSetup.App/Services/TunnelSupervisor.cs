using System.Diagnostics;
using System.IO;
using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Tunnel;

namespace CodexSshSetup.App.Services;

public enum TunnelState
{
    Disconnected,
    Connecting,
    Connected,
    WaitingToReconnect,
    Stopping,
    Failed
}

public interface ITunnelProcess : IAsyncDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForReadyAsync(CancellationToken cancellationToken);
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
    void Kill();
}

public interface ITunnelProcessFactory
{
    ITunnelProcess Start(CommandSpec command);
}

public sealed class TunnelSupervisor : IAsyncDisposable
{
    private readonly ITunnelProcessFactory _factory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private ITunnelProcess? _ownedProcess;

    public TunnelSupervisor(
        ITunnelProcessFactory factory,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _factory = factory;
        _delay = delay ?? Task.Delay;
    }

    public TunnelState State { get; private set; } = TunnelState.Disconnected;
    public event EventHandler<TunnelState>? StateChanged;

    public async Task ConnectAsync(ServerProfile profile, int localProxyPort)
    {
        TaskCompletionSource started;
        await _gate.WaitAsync();
        try
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _stop?.Dispose();
            _stop = new CancellationTokenSource();
            started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = RunLoopAsync(profile, localProxyPort, started, _stop.Token);
        }
        finally
        {
            _gate.Release();
        }

        await started.Task;
    }

    public async Task DisconnectAsync()
    {
        Task? loop;
        await _gate.WaitAsync();
        try
        {
            if (_loop is null)
            {
                SetState(TunnelState.Disconnected);
                return;
            }

            SetState(TunnelState.Stopping);
            var ownedProcess = _ownedProcess;
            ownedProcess?.Kill();
            _stop?.Cancel();
            loop = _loop;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
        }

        await _gate.WaitAsync();
        try
        {
            _loop = null;
            _ownedProcess = null;
            SetState(TunnelState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunLoopAsync(
        ServerProfile profile,
        int localProxyPort,
        TaskCompletionSource started,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(TunnelState.Connecting);
            ITunnelProcess? process = null;
            try
            {
                process = _factory.Start(SshCommandFactory.CreateTunnel(profile, localProxyPort));
                _ownedProcess = process;
                await process.WaitForReadyAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                SetState(TunnelState.Connected);
                started.TrySetResult();
                var connectedAt = DateTime.UtcNow;
                await process.WaitForExitAsync(cancellationToken);
                if (DateTime.UtcNow - connectedAt > TimeSpan.FromSeconds(30))
                {
                    attempt = 0;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    SetState(TunnelState.WaitingToReconnect);
                    await _delay(RetryPolicy.DelayForAttempt(attempt++), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                started.TrySetCanceled(cancellationToken);
                break;
            }
            catch (Exception exception)
            {
                SetState(TunnelState.Failed);
                if (!started.Task.IsCompleted)
                {
                    started.TrySetException(exception);
                    return;
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    await _delay(RetryPolicy.DelayForAttempt(attempt++), cancellationToken);
                }
            }
            finally
            {
                if (ReferenceEquals(_ownedProcess, process)) _ownedProcess = null;
                if (process is not null)
                {
                    process.Kill();
                    await process.DisposeAsync();
                }
            }
        }
    }

    private void SetState(TunnelState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _stop?.Dispose();
        _gate.Dispose();
    }
}

public sealed class SystemTunnelProcessFactory(Action<string>? log = null) : ITunnelProcessFactory
{
    public ITunnelProcess Start(CommandSpec command)
    {
        var info = ProcessRunner.CreateStartInfo(command);
        info.RedirectStandardInput = true;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        return new SystemTunnelProcess(process, log);
    }
}

public sealed class SystemTunnelProcess : ITunnelProcess
{
    private readonly Process process;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _heartbeatStop = new();
    private readonly Task _heartbeat;

    public SystemTunnelProcess(Process process, Action<string>? log = null)
    {
        this.process = process;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data == TunnelHeartbeatProtocol.Ready)
            {
                _ready.TrySetResult();
                log?.Invoke("远端已确认隧道就绪；双向心跳已启用，失联后自动释放本隧道。");
            }
            else if (args.Data is not null && args.Data != TunnelHeartbeatProtocol.Acknowledgement)
                log?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) log?.Invoke(args.Data); };
        process.Exited += (_, _) => _ready.TrySetException(new InvalidOperationException(
            $"SSH 隧道在远端确认就绪前退出（退出码 {process.ExitCode}）。请查看端口占用或连接日志。"));
        if (!process.Start()) throw new InvalidOperationException("无法启动 SSH 隧道进程。");
        process.StandardInput.NewLine = "\n";
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _heartbeat = SendHeartbeatsAsync(_heartbeatStop.Token);
    }

    public bool HasExited => process.HasExited;
    public int? ExitCode => process.HasExited ? process.ExitCode : null;
    public Task WaitForReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    private async Task SendHeartbeatsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                await process.StandardInput.WriteLineAsync(TunnelHeartbeatProtocol.Heartbeat.AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                await Task.Delay(TunnelHeartbeatProtocol.Interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _ready.TrySetException(new InvalidOperationException("SSH 隧道心跳通道已关闭。", exception));
            Kill();
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        return process.ExitCode;
    }

    public void Kill()
    {
        _heartbeatStop.Cancel();
        try
        {
            process.StandardInput.Close();
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await _heartbeat;
        await process.WaitForExitAsync();
        process.WaitForExit();
        process.Dispose();
        _heartbeatStop.Dispose();
    }
}
