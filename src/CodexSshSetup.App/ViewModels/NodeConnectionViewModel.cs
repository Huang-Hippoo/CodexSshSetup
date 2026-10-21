using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.App.Models;
using CodexSshSetup.App.Services;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Validation;

namespace CodexSshSetup.App.ViewModels;

public sealed class NodeConnectionViewModel : BindableBase, IAsyncDisposable
{
    private readonly FileLog _log;
    private readonly Func<NodeConnectionViewModel, SavedConnection, CancellationToken, Task> _save;
    private readonly SshProvisioningService _ssh;
    private readonly ProxyDetectionService _proxy = new();
    private readonly CodexRemoteService _codex;
    private readonly TunnelSupervisor _tunnel;
    private readonly RemotePreparationService _preparation;
    private CancellationTokenSource? _operation;
    private string _host = string.Empty;
    private string _userName = string.Empty;
    private string _port = "22";
    private string _alias = "node02";
    private string _proxyPort = "7890";
    private string _logText = string.Empty;
    private string _headline = "填写服务器信息后开始首次配置";
    private bool _isBusy;
    private bool _sessionRequested;
    private bool _sessionExistedAtOperationStart;
    private readonly int _remoteProxyPort;
    private TaskCompletionSource? _operationFinished;

    public NodeConnectionViewModel(SavedConnection? saved, FileLog log,
        Func<NodeConnectionViewModel, SavedConnection, CancellationToken, Task> save, string defaultAlias = "server")
    {
        _log = log;
        _save = save;
        Saved = saved;
        _remoteProxyPort = 17890;
        if (saved is { } item)
        {
            Host = item.Profile.Host;
            UserName = item.Profile.UserName;
            Port = item.Profile.Port.ToString();
            Alias = item.Profile.Alias;
            ProxyPort = (item.LocalProxyPort ?? 7890).ToString();
            Headline = "已保存，可直接连接；选择其他节点不会断开此节点";
        }
        else Alias = defaultAlias;
        var runner = new ConnectionProcessRunner(new ProcessRunner(), () =>
            new ServerProfile(Host.Trim(), UserName.Trim(), int.Parse(Port), Alias.Trim(), _remoteProxyPort));
        _ssh = new SshProvisioningService(runner, _log);
        _codex = new CodexRemoteService(runner);
        _preparation = new RemotePreparationService(runner, _ssh);
        _tunnel = new TunnelSupervisor(new SystemTunnelProcessFactory(AppendLog));
        _tunnel.StateChanged += (_, state) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            TunnelStatus.Update(state switch
            {
                TunnelState.Connected => ComponentState.Ready,
                TunnelState.Connecting or TunnelState.WaitingToReconnect => ComponentState.Checking,
                TunnelState.Failed => ComponentState.Failed,
                _ => ComponentState.Unknown
            }, TunnelMessage(state));
            Raise(nameof(ConnectionLabel));
            Raise(nameof(CurrentTunnelState));
            if (!IsBusy) Headline = state == TunnelState.Connected ? "Codex SSH 已就绪" : TunnelMessage(state);
            Raise(nameof(IsEditable));
        });

        ConfigureCommand = new AsyncCommand(ConfigureAsync, () => IsEditable);
        ConnectCommand = new AsyncCommand(ConnectDailyAsync, () => !IsBusy && !HasTunnel);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => !IsBusy && HasTunnel);
        SaveCommand = new AsyncCommand(SaveOnlyAsync, () => IsEditable);
        RedetectCommand = new AsyncCommand(RedetectAsync, () => !IsBusy);
        LoginCommand = new AsyncCommand(DeviceLoginAsync, () => !IsBusy);
        CancelCommand = new AsyncCommand(CancelAsync, () => IsBusy);
        OpenLogsCommand = new AsyncCommand(OpenLogsAsync);
        DetectProxyCommand = new AsyncCommand(() => RunBusyAsync(async token =>
        {
            await DetectProxyAsync(token);
            Headline = $"已找到电脑代理 127.0.0.1:{ProxyPort}，连接后会自动映射到远端 17890";
        }), () => IsEditable);
    }

    public string Host { get => _host; set { if (Set(ref _host, value)) Raise(nameof(Endpoint)); } }
    public string UserName { get => _userName; set { if (Set(ref _userName, value)) Raise(nameof(Endpoint)); } }
    public string Port { get => _port; set { if (Set(ref _port, value)) Raise(nameof(Endpoint)); } }
    public string Alias { get => _alias; set { if (Set(ref _alias, value)) Raise(nameof(DisplayName)); } }
    public string ProxyPort { get => _proxyPort; set { if (Set(ref _proxyPort, value)) Raise(nameof(ProxyMapping)); } }
    public string ProxyMapping => $"远端 127.0.0.1:17890 → 电脑 127.0.0.1:{(string.IsNullOrWhiteSpace(ProxyPort) ? "自动检测" : ProxyPort)}";
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? "未命名节点" : Alias;
    public string Endpoint => $"{UserName}@{Host}:{Port}";
    public SavedConnection? Saved { get; private set; }
    public bool HasTunnel => _sessionRequested;
    public bool IsEditable => !IsBusy && !HasTunnel;
    public TunnelState CurrentTunnelState => _tunnel.State;
    public string ConnectionLabel => IsBusy && !HasTunnel ? "正在检查…" : _tunnel.State switch
    {
        TunnelState.Connected => "已连接",
        TunnelState.Connecting => "正在连接…",
        TunnelState.WaitingToReconnect => "等待重连…",
        TunnelState.Stopping => "正在断开…",
        TunnelState.Failed => HasTunnel ? "重试中…" : "连接失败",
        _ => "未连接"
    };
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ConfigureCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
            RedetectCommand.RaiseCanExecuteChanged();
            LoginCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
            DetectProxyCommand.RaiseCanExecuteChanged();
            Raise(nameof(IsEditable));
            Raise(nameof(ConnectionLabel));
        }
    }

    public StatusCardViewModel VpnStatus { get; } = new("本机代理");
    public StatusCardViewModel SshStatus { get; } = new("SSH");
    public StatusCardViewModel TunnelStatus { get; } = new("隧道");
    public StatusCardViewModel CodexStatus { get; } = new("Codex CLI");
    public StatusCardViewModel LoginStatus { get; } = new("登录");
    public StatusCardViewModel RemoteProxyStatus { get; } = new("远端代理");
    public AsyncCommand ConfigureCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand RedetectCommand { get; }
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand OpenLogsCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DetectProxyCommand { get; }

    public void CommitSaved(SavedConnection saved)
    {
        Saved = saved;
        Raise(nameof(Saved));
    }

    private async Task SaveOnlyAsync()
    {
        if (!TryBuildProfile(out var profile)) return;
        await RunBusyAsync(async token =>
        {
            await SaveAsync(profile, token);
            Headline = "节点已保存，下次可直接选择连接";
        });
    }

    private async Task ConfigureAsync()
    {
        if (!TryBuildProfile(out var profile)) return;
        await RunBusyAsync(async token =>
        {
            Headline = "正在执行首次配置…";
            await SaveAsync(profile, token);
            SshStatus.Update(ComponentState.Checking, "测试端口连通性");
            await _ssh.TestPortAsync(profile, token);
            AppendLog("SSH 端口可访问。");
            var keyPath = await _ssh.EnsureKeyAsync(profile, Progress(), token);
            AppendLog("正在安装 SSH 公钥（最多 5 分钟）；将打开系统 OpenSSH 窗口，密码只在该窗口中输入。");
            await _ssh.InstallPublicKeyAsync(profile, keyPath, token);
            await _ssh.UpdateSshConfigAsync(profile, keyPath, token);
            await _ssh.TestPasswordlessAsync(profile, Progress(), token);
            SshStatus.Update(ComponentState.Ready, "免密登录正常");

            var localProxyPort = await DetectProxyAsync(token);
            await SaveAsync(profile, token);
            await PrepareConnectedAsync(profile, localProxyPort, token);
            Headline = "该 SSH 主机已准备好，请回 Codex Desktop 的 设置 → 连接 → SSH 刷新";
            AppendLog("首次配置流程完成。");
        });
    }

    private async Task ConnectDailyAsync()
    {
        if (!TryBuildProfile(out var profile)) return;
        await RunBusyAsync(async token =>
        {
            Headline = "正在执行日常检查…";
            await SaveAsync(profile, token);
            var localProxyPort = await DetectProxyAsync(token);
            SshStatus.Update(ComponentState.Checking, "测试免密登录");
            await _ssh.TestPasswordlessAsync(profile, Progress(), token);
            SshStatus.Update(ComponentState.Ready, "免密登录正常");
            await SaveAsync(profile, token);
            await PrepareConnectedAsync(profile, localProxyPort, token);
            Headline = "Codex SSH 已就绪";
        });
    }

    private async Task<int> DetectProxyAsync(CancellationToken token)
    {
        VpnStatus.Update(ComponentState.Checking, "检测进程和 CONNECT");
        var preferred = int.TryParse(ProxyPort, out var parsed) ? parsed : (int?)null;
        var result = await _proxy.DetectAsync(preferred, token);
        if (!result.ConnectSucceeded || result.Port is null)
        {
            VpnStatus.Update(result.ProcessRunning ? ComponentState.Warning : ComponentState.Failed, result.Message);
            throw new InvalidOperationException(result.Message);
        }
        ProxyPort = result.Port.Value.ToString();
        VpnStatus.Update(ComponentState.Ready, $"127.0.0.1:{result.Port}");
        AppendLog($"已匹配代理：{ProxyMapping}");
        return result.Port.Value;
    }

    private async Task PrepareConnectedAsync(ServerProfile profile, int localProxyPort, CancellationToken token)
    {
        await ConnectTunnelAsync(profile, localProxyPort);
        RemoteProxyStatus.Update(ComponentState.Checking, "检查当前账号配置");
        try
        {
            var matched = await _preparation.EnsureUserProxyAsync(profile, Progress(), token);
            RemoteProxyStatus.Update(matched ? ComponentState.Ready : ComponentState.Warning,
                matched ? "127.0.0.1:17890" : "已有其他代理，已保留");
        }
        catch { RemoteProxyStatus.Update(ComponentState.Failed, "配置失败，查看日志"); throw; }
        CodexStatus.Update(ComponentState.Checking, "检测；缺失时自动安装");
        try
        {
            var tools = await _preparation.EnsureCodexAsync(profile, Progress(), token);
            CodexStatus.Update(ComponentState.Ready, tools.CodexVersion);
        }
        catch { CodexStatus.Update(ComponentState.Failed, "准备失败，查看日志"); throw; }
        await RefreshLoginAsync(profile, token);
    }

    private async Task RefreshLoginAsync(ServerProfile profile, CancellationToken token)
    {
        LoginStatus.Update(ComponentState.Checking, "检测登录状态");
        var result = await _codex.GetLoginStatusAsync(profile, Progress(), token);
        LoginStatus.Update(result.LoggedIn ? ComponentState.Ready : ComponentState.Warning,
            result.LoggedIn ? result.Message : "未登录；请点击设备登录");
    }

    private async Task DeviceLoginAsync()
    {
        if (!TryBuildProfile(out var profile)) return;
        await RunBusyAsync(async token =>
        {
            await SaveAsync(profile, token);
            if (_tunnel.State != TunnelState.Connected)
            {
                var localProxyPort = await DetectProxyAsync(token);
                await ConnectTunnelAsync(profile, localProxyPort);
            }
            Headline = "请在日志区域复制设备登录网址和验证码；验证码不会写入磁盘";
            LoginStatus.Update(ComponentState.Checking, "等待设备授权");
            var result = await _codex.DeviceLoginAsync(profile, Progress(), token);
            if (!result.Success) throw new InvalidOperationException(SshProvisioningService.MapError(result.StandardError));
            await RefreshLoginAsync(profile, token);
        });
    }

    private async Task RedetectAsync()
    {
        if (!TryBuildProfile(out var profile)) return;
        await RunBusyAsync(async token =>
        {
            await DetectProxyAsync(token);
            await _ssh.TestPasswordlessAsync(profile, Progress(), token);
            SshStatus.Update(ComponentState.Ready, "免密登录正常");
            var tools = await _ssh.InspectToolsAsync(profile, Progress(), token);
            CodexStatus.Update(tools.IsReadyForDesktop ? ComponentState.Ready : ComponentState.Failed,
                tools.IsReadyForDesktop ? tools.CodexVersion : "专用 wrapper 未安装");
            await RefreshLoginAsync(profile, token);
            Headline = "状态已刷新";
        });
    }

    private async Task DisconnectAsync()
    {
        await _tunnel.DisconnectAsync();
        SetSessionRequested(false);
        Headline = "隧道已断开";
    }

    private async Task CancelAsync()
    {
        _operation?.Cancel();
        AppendLog("正在取消当前节点的操作…");
        if (!_sessionExistedAtOperationStart) await DisconnectAsync();
    }

    private void SetSessionRequested(bool value)
    {
        _sessionRequested = value;
        Raise(nameof(HasTunnel));
        Raise(nameof(IsEditable));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        DetectProxyCommand.RaiseCanExecuteChanged();
        ConfigureCommand.RaiseCanExecuteChanged();
    }

    private async Task ConnectTunnelAsync(ServerProfile profile, int proxyPort)
    {
        _operation?.Token.ThrowIfCancellationRequested();
        SetSessionRequested(true);
        try { await _tunnel.ConnectAsync(profile, proxyPort); }
        catch { SetSessionRequested(false); throw; }
    }

    private Task OpenLogsAsync()
    {
        Directory.CreateDirectory(_log.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", _log.DirectoryPath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _sessionExistedAtOperationStart = HasTunnel;
        _operationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        _operation = new CancellationTokenSource();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Headline = "操作已取消"; AppendLog("操作已取消。"); }
        catch (TimeoutException exception) { Headline = "操作超时，请查看当前步骤"; AppendLog($"超时：{exception.Message}"); }
        catch (Exception exception) { Headline = "操作失败，请查看原因与日志"; AppendLog($"失败：{exception.Message}"); }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; _operationFinished.TrySetResult(); }
    }

    public bool TryBuildProfile(out ServerProfile profile)
    {
        var port = int.TryParse(Port, out var parsed) ? parsed : 0;
        profile = new ServerProfile(Host.Trim(), UserName.Trim(), port, Alias.Trim(), _remoteProxyPort);
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count == 0) return true;
        Headline = string.Join(" ", errors);
        AppendLog($"输入错误：{Headline}");
        return false;
    }

    private Task SaveAsync(ServerProfile profile, CancellationToken token)
    {
        var proxy = int.TryParse(ProxyPort, out var parsed) ? parsed : (int?)null;
        if (proxy is < 1 or > 65535 || (!string.IsNullOrWhiteSpace(ProxyPort) && proxy is null))
            throw new ArgumentException("本机代理端口必须在 1 到 65535 之间。");
        return _save(this, new SavedConnection(profile, proxy), token);
    }

    private IProgress<string> Progress() => new Progress<string>(AppendLog);

    private void AppendLog(string line)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }
        var builder = new StringBuilder(LogText);
        builder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("][").Append(Alias).Append("] ").AppendLine(line);
        if (builder.Length > 80_000) builder.Remove(0, builder.Length - 60_000);
        LogText = builder.ToString();
        _ = _log.WriteAsync($"[{Alias}] {line}");
    }

    private string TunnelMessage(TunnelState state) => state switch
    {
        TunnelState.Connected => $"127.0.0.1:{_remoteProxyPort} 已转发",
        TunnelState.Connecting => "正在建立",
        TunnelState.WaitingToReconnect => "断线，等待自动重连",
        TunnelState.Stopping => "正在停止",
        TunnelState.Failed => "启动失败，请查看日志",
        _ => "未连接"
    };

    public async Task StopTunnelAsync()
    {
        _operation?.Cancel();
        await DisconnectAsync();
        if (_operationFinished is { } finished) await finished.Task;
    }
    public async ValueTask DisposeAsync() { await StopTunnelAsync(); await _tunnel.DisposeAsync(); }
}
