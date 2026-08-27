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

public sealed class MainViewModel : BindableBase, IAsyncDisposable
{
    private readonly AppConfigStore _configStore = new();
    private readonly FileLog _log = new();
    private readonly SshProvisioningService _ssh;
    private readonly ProxyDetectionService _proxy = new();
    private readonly CodexRemoteService _codex;
    private readonly TunnelSupervisor _tunnel;
    private CancellationTokenSource? _operation;
    private string _host = string.Empty;
    private string _userName = string.Empty;
    private string _port = "22";
    private string _alias = "node02";
    private string _proxyPort = "7890";
    private string _logText = string.Empty;
    private string _headline = "填写服务器信息后开始首次配置";
    private bool _isBusy;

    public MainViewModel()
    {
        var runner = new ProcessRunner();
        _ssh = new SshProvisioningService(runner, _log);
        _codex = new CodexRemoteService(runner);
        _tunnel = new TunnelSupervisor(new SystemTunnelProcessFactory(AppendLog));
        _tunnel.StateChanged += (_, state) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
            TunnelStatus.Update(state switch
            {
                TunnelState.Connected => ComponentState.Ready,
                TunnelState.Connecting or TunnelState.WaitingToReconnect => ComponentState.Checking,
                TunnelState.Failed => ComponentState.Failed,
                _ => ComponentState.Unknown
            }, TunnelMessage(state)));

        ConfigureCommand = new AsyncCommand(ConfigureAsync, () => !IsBusy);
        ConnectCommand = new AsyncCommand(ConnectDailyAsync, () => !IsBusy);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, () => !IsBusy);
        RedetectCommand = new AsyncCommand(RedetectAsync, () => !IsBusy);
        LoginCommand = new AsyncCommand(DeviceLoginAsync, () => !IsBusy);
        CancelCommand = new AsyncCommand(CancelAsync, () => IsBusy);
        OpenLogsCommand = new AsyncCommand(OpenLogsAsync);
    }

    public string Host { get => _host; set => Set(ref _host, value); }
    public string UserName { get => _userName; set => Set(ref _userName, value); }
    public string Port { get => _port; set => Set(ref _port, value); }
    public string Alias { get => _alias; set => Set(ref _alias, value); }
    public string ProxyPort { get => _proxyPort; set => Set(ref _proxyPort, value); }
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string Headline { get => _headline; private set => Set(ref _headline, value); }
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
        }
    }

    public StatusCardViewModel VpnStatus { get; } = new("iKuuuVPN");
    public StatusCardViewModel SshStatus { get; } = new("SSH");
    public StatusCardViewModel TunnelStatus { get; } = new("Tunnel");
    public StatusCardViewModel CodexStatus { get; } = new("Codex CLI");
    public StatusCardViewModel LoginStatus { get; } = new("Login");
    public AsyncCommand ConfigureCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand RedetectCommand { get; }
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand OpenLogsCommand { get; }

    public async Task InitializeAsync()
    {
        var config = await _configStore.LoadAsync(CancellationToken.None);
        if (config.Profile is { } profile)
        {
            Host = profile.Host;
            UserName = profile.UserName;
            Port = profile.Port.ToString();
            Alias = profile.Alias;
            Headline = "已载入主机配置，可执行日常一键连接";
        }
        if (config.LocalProxyPort is { } proxyPort) ProxyPort = proxyPort.ToString();
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
            AppendLog("将打开系统 OpenSSH 窗口；密码只在该窗口中输入。");
            await _ssh.InstallPublicKeyAsync(profile, keyPath, token);
            await _ssh.UpdateSshConfigAsync(profile, keyPath, token);
            await _ssh.TestPasswordlessAsync(profile, Progress(), token);
            SshStatus.Update(ComponentState.Ready, "免密登录正常");

            CodexStatus.Update(ComponentState.Checking, "检测 Node/npm/Codex");
            var tools = await _ssh.InspectToolsAsync(profile, Progress(), token);
            if (!tools.HasNode20OrNewer || !tools.HasNpm) await _ssh.EnsureNodeAsync(profile, Progress(), token);
            tools = await _ssh.InspectToolsAsync(profile, Progress(), token);
            if (!tools.IsReadyForDesktop)
            {
                await _ssh.InstallCodexAsync(profile, Progress(), token);
                tools = await _ssh.InspectToolsAsync(profile, Progress(), token);
            }
            if (!tools.IsReadyForDesktop) throw new InvalidOperationException("Codex 安装后仍无法找到本工具的专用 wrapper。");
            CodexStatus.Update(ComponentState.Ready, tools.CodexVersion);

            var localProxyPort = await DetectProxyAsync(token);
            await _tunnel.ConnectAsync(profile, localProxyPort);
            await RefreshLoginAsync(profile, token);
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
            var localProxyPort = await DetectProxyAsync(token);
            SshStatus.Update(ComponentState.Checking, "测试免密登录");
            await _ssh.TestPasswordlessAsync(profile, Progress(), token);
            SshStatus.Update(ComponentState.Ready, "免密登录正常");
            CodexStatus.Update(ComponentState.Checking, "检测远端 Codex");
            var tools = await _ssh.InspectToolsAsync(profile, Progress(), token);
            CodexStatus.Update(tools.IsReadyForDesktop ? ComponentState.Ready : ComponentState.Failed,
                tools.IsReadyForDesktop ? tools.CodexVersion : "未安装专用 wrapper；请运行首次配置");
            if (!tools.IsReadyForDesktop) return;
            await _tunnel.ConnectAsync(profile, localProxyPort);
            await RefreshLoginAsync(profile, token);
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
        return result.Port.Value;
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
            if (_tunnel.State != TunnelState.Connected)
            {
                var localProxyPort = await DetectProxyAsync(token);
                await _tunnel.ConnectAsync(profile, localProxyPort);
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
        Headline = "隧道已断开";
    }

    private Task CancelAsync() { _operation?.Cancel(); AppendLog("正在取消当前操作…"); return Task.CompletedTask; }

    private Task OpenLogsAsync()
    {
        Directory.CreateDirectory(_log.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", _log.DirectoryPath) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        _operation = new CancellationTokenSource();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Headline = "操作已取消"; AppendLog("操作已取消。"); }
        catch (Exception exception) { Headline = "操作失败，请查看原因与日志"; AppendLog($"失败：{exception.Message}"); }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; }
    }

    private bool TryBuildProfile(out ServerProfile profile)
    {
        var port = int.TryParse(Port, out var parsed) ? parsed : 0;
        profile = new ServerProfile(Host.Trim(), UserName.Trim(), port, Alias.Trim());
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count == 0) return true;
        Headline = string.Join(" ", errors);
        AppendLog($"输入错误：{Headline}");
        return false;
    }

    private Task SaveAsync(ServerProfile profile, CancellationToken token)
    {
        var proxy = int.TryParse(ProxyPort, out var parsed) ? parsed : (int?)null;
        return _configStore.SaveAsync(new AppConfig { Profile = profile, LocalProxyPort = proxy }, token);
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
        builder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(line);
        if (builder.Length > 80_000) builder.Remove(0, builder.Length - 60_000);
        LogText = builder.ToString();
        _ = _log.WriteAsync(line);
    }

    private static string TunnelMessage(TunnelState state) => state switch
    {
        TunnelState.Connected => "127.0.0.1:17890 已转发",
        TunnelState.Connecting => "正在建立",
        TunnelState.WaitingToReconnect => "断线，等待自动重连",
        TunnelState.Stopping => "正在停止",
        TunnelState.Failed => "启动失败，请查看日志",
        _ => "未连接"
    };

    public Task StopTunnelAsync() => _tunnel.DisconnectAsync();
    public async ValueTask DisposeAsync() { _operation?.Cancel(); await _tunnel.DisposeAsync(); }
}
