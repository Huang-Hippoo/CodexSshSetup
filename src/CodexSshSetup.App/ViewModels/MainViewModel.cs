using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.App.Services;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Validation;

namespace CodexSshSetup.App.ViewModels;

public sealed class MainViewModel : BindableBase, IAsyncDisposable
{
    private readonly ConnectionCatalogStore _store = new();
    private readonly FileLog _log = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private NodeConnectionViewModel? _selected;
    private bool _initialized;
    private bool _closing;
    private string _notice = "正在载入已保存的节点…";

    public MainViewModel()
    {
        AddCommand = new AsyncCommand(AddAsync, () => !_closing);
        RemoveCommand = new AsyncCommand(() => GuardAsync(RemoveAsync), () => SelectedNode?.IsEditable == true);
        ConnectCommand = new AsyncCommand(() => SelectedNode?.ConnectCommand.ExecuteAsync() ?? Task.CompletedTask,
            () => !_closing && SelectedNode?.ConnectCommand.CanExecute(null) == true);
        DisconnectCommand = new AsyncCommand(() => GuardAsync(DisconnectAllAsync), () => Nodes.Any(node => node.HasTunnel || node.IsBusy));
        ConnectAllCommand = new AsyncCommand(() => GuardAsync(ConnectAllAsync), () => !_closing && Nodes.Any(node => node.Saved is not null && !node.HasTunnel && !node.IsBusy));
    }

    public ObservableCollection<NodeConnectionViewModel> Nodes { get; } = [];
    public NodeConnectionViewModel? SelectedNode
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            RefreshCommands();
            if (_initialized && !_closing) _ = GuardAsync(PersistAsync);
        }
    }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string ConnectionSummary => $"已连接 {Nodes.Count(node => node.CurrentTunnelState == TunnelState.Connected)} / {Nodes.Count} 个节点";
    public AsyncCommand AddCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand ConnectAllCommand { get; }

    public async Task InitializeAsync()
    {
        await GuardAsync(async () =>
        {
            var sshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            var sshConfig = File.Exists(sshPath) ? await File.ReadAllTextAsync(sshPath) : "";
            var catalog = await _store.LoadAsync(sshConfig, CancellationToken.None);
            foreach (var saved in catalog.Connections)
            {
                if (saved?.Profile is not { } profile || ProfileValidator.Validate(profile).Count != 0 ||
                    Nodes.Any(node => string.Equals(node.Alias, profile.Alias, StringComparison.OrdinalIgnoreCase))) continue;
                AddNode(saved with { Profile = profile with { RemoteProxyPort = 17890 } });
            }
            SelectedNode = Nodes.FirstOrDefault(node => string.Equals(node.Alias, catalog.SelectedAlias, StringComparison.OrdinalIgnoreCase)) ?? Nodes.FirstOrDefault();
            if (SelectedNode is null) await AddAsync();
            await PersistAsync();
            _initialized = true;
            Notice = "选择节点即可连接；多个节点可同时在线，各自使用远端 127.0.0.1:17890。";
        });
    }

    private NodeConnectionViewModel AddNode(SavedConnection? saved, string defaultAlias = "server")
    {
        var node = new NodeConnectionViewModel(saved, _log, SaveNodeAsync, defaultAlias);
        node.PropertyChanged += NodeChanged;
        Nodes.Add(node);
        RefreshCommands();
        return node;
    }

    private Task AddAsync()
    {
        var number = 1;
        while (Nodes.Any(node => string.Equals(node.Alias, $"server{number}", StringComparison.OrdinalIgnoreCase))) number++;
        SelectedNode = AddNode(null, $"server{number}");
        Notice = "填写新节点后点击“保存节点”；已有免密 SSH 和 Codex 环境可直接连接。";
        return Task.CompletedTask;
    }

    private async Task SaveNodeAsync(NodeConnectionViewModel node, SavedConnection saved, CancellationToken token)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            if (Nodes.Any(other => other != node && string.Equals(other.Alias.Trim(), saved.Profile.Alias, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("这个 SSH 别名已经在列表中，请选择已有节点或使用其他别名。");
            foreach (var other in Nodes.Where(other => other != node && (other.HasTunnel || other.IsBusy)))
            {
                if (other.TryBuildProfile(out var active) && ConnectionHistory.SharesTunnelEndpoint(saved.Profile, active))
                    throw new InvalidOperationException($"{other.DisplayName} 正在使用同一服务器的 17890 端口，请使用该连接，或先断开它。");
            }
            var entries = Nodes.Where(other => other != node && other.Saved is not null).Select(other => other.Saved!).ToList();
            entries.Add(saved);
            await _store.SaveAsync(new ConnectionCatalog
            {
                Connections = entries,
                SelectedAlias = SelectedNode == node ? saved.Profile.Alias : SelectedNode?.Saved?.Profile.Alias
            }, token);
            node.CommitSaved(saved);
            Notice = $"已保存 {saved.Profile.Alias}。其他节点的连接不受影响。";
            RefreshCommands();
        }
        finally { _saveGate.Release(); }
    }

    private async Task PersistAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            await _store.SaveAsync(new ConnectionCatalog
            {
                Connections = Nodes.Where(node => node.Saved is not null).Select(node => node.Saved!).ToList(),
                SelectedAlias = SelectedNode?.Saved?.Profile.Alias
            }, CancellationToken.None);
        }
        finally { _saveGate.Release(); }
    }

    private async Task RemoveAsync()
    {
        var node = SelectedNode;
        if (node?.IsEditable != true) return;
        if (System.Windows.MessageBox.Show($"从已保存节点中移除 {node.DisplayName}？服务器、SSH 密钥和 SSH 配置会保留。", "移除节点",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        node.PropertyChanged -= NodeChanged;
        Nodes.Remove(node);
        await node.DisposeAsync();
        SelectedNode = Nodes.FirstOrDefault();
        if (SelectedNode is null) await AddAsync();
        await PersistAsync();
        RefreshCommands();
    }

    private async Task ConnectAllAsync()
    {
        var candidates = new List<NodeConnectionViewModel>();
        foreach (var node in Nodes.Where(node => node.Saved is not null && !node.IsBusy && !node.HasTunnel))
        {
            if (!node.TryBuildProfile(out var profile)) continue;
            if (candidates.Any(other => other.TryBuildProfile(out var queued) && ConnectionHistory.SharesTunnelEndpoint(queued, profile)) ||
                Nodes.Any(other => other != node && (other.HasTunnel || other.IsBusy) && other.Saved is { } saved &&
                    ConnectionHistory.SharesTunnelEndpoint(saved.Profile, profile))) continue;
            candidates.Add(node);
        }
        Notice = $"正在并行连接 {candidates.Count} 个节点；同一服务器的重复别名会自动跳过。";
        await Task.WhenAll(candidates.Select(node => node.ConnectCommand.ExecuteAsync()));
        Notice = $"连接检查已完成。{ConnectionSummary}；失败原因显示在对应节点中。";
    }

    private async Task DisconnectAllAsync()
    {
        await Task.WhenAll(Nodes.Select(node => node.StopTunnelAsync()));
        Notice = "已断开本程序管理的全部隧道。";
    }

    private void NodeChanged(object? sender, PropertyChangedEventArgs e) => RefreshCommands();
    private void RefreshCommands()
    {
        Raise(nameof(ConnectionSummary));
        AddCommand?.RaiseCanExecuteChanged(); RemoveCommand?.RaiseCanExecuteChanged();
        ConnectCommand?.RaiseCanExecuteChanged(); DisconnectCommand?.RaiseCanExecuteChanged(); ConnectAllCommand?.RaiseCanExecuteChanged();
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            Notice = exception.Message;
            await _log.WriteAsync($"[节点列表] {exception.Message}");
        }
    }

    public async Task StopTunnelAsync() { _closing = true; await DisconnectAllAsync(); }
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await Task.WhenAll(Nodes.Select(async node => await node.DisposeAsync()));
    }
}
