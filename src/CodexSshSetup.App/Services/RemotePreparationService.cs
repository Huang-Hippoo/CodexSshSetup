using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.App.Models;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.App.Services;

public sealed class RemotePreparationService(IProcessRunner runner, SshProvisioningService ssh)
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public async Task<bool> EnsureUserProxyAsync(ServerProfile profile, IProgress<string>? progress, CancellationToken token)
    {
        progress?.Report("检测当前账号代理配置；仅补齐缺失项，已有非空配置保留…");
        var result = await runner.RunAsync(RemoteSetupCommands.EnsureUserProxy(profile), progress, token);
        if (!result.Success) throw new InvalidOperationException($"用户代理配置失败：{result.StandardError.Trim()}");
        if (result.StandardOutput.Contains("PROXY_CONFIG=existing_different", StringComparison.Ordinal))
        {
            progress?.Report("已有代理值与本隧道不同，已保留；Codex 专用启动器和下载过程会使用本节点的 17890 隧道。");
            return false;
        }
        return true;
    }

    public async Task<RemoteToolInfo> EnsureCodexAsync(ServerProfile profile, IProgress<string>? progress, CancellationToken token)
    {
        var tools = await ssh.InspectToolsAsync(profile, progress, token);
        if (tools.IsReadyForDesktop) return tools;
        await InstallGate.WaitAsync(token);
        try
        {
            // Another node sharing this home may have completed installation.
            tools = await ssh.InspectToolsAsync(profile, progress, token);
            if (tools.IsReadyForDesktop) return tools;
            if (!tools.HasCodex && (!tools.HasNode20OrNewer || !tools.HasNpm))
            {
                progress?.Report("Codex 和所需 Node/npm 不完整，正在准备用户级运行环境…");
                await ssh.EnsureNodeAsync(profile, progress, token);
            }
            progress?.Report(tools.HasCodex ? "复用已有 Codex，仅补齐用户级专用启动器…" : "远端未安装 Codex，正在通过已建立的代理隧道下载到当前账号…");
            var result = await runner.RunAsync(RemoteSetupCommands.EnsureCodex(profile), progress, token);
            if (!result.Success) throw new InvalidOperationException($"准备 Codex 失败：{result.StandardError.Trim()}");
            tools = await ssh.InspectToolsAsync(profile, progress, token);
            if (!tools.IsReadyForDesktop) throw new InvalidOperationException("安装后未检测到可用 Codex 或专用启动器，请检查节点日志。");
            return tools;
        }
        finally { InstallGate.Release(); }
    }
}
