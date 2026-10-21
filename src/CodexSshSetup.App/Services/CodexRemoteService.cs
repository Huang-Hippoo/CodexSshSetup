using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.App.Services;

public sealed record LoginStatus(bool LoggedIn, string Message);

public sealed class CodexRemoteService(IProcessRunner runner)
{
    public async Task<LoginStatus> GetLoginStatusAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("正在检查远端 Codex 登录状态（最多 60 秒）…");
        var command = new CommandSpec("ssh.exe",
            [profile.Alias, "$HOME/.local/bin/codex-codex-ssh-setup login status"],
            Timeout: TimeSpan.FromSeconds(60));
        var result = await runner.RunAsync(command, progress, cancellationToken);
        var combined = result.StandardOutput + result.StandardError;
        var loggedIn = result.Success &&
            combined.Contains("Logged in using ChatGPT", StringComparison.OrdinalIgnoreCase);
        return new LoginStatus(loggedIn,
            loggedIn ? "Logged in using ChatGPT" : combined.Trim());
    }

    public Task<ProcessResult> DeviceLoginAsync(
        ServerProfile profile, IProgress<string> progress, CancellationToken cancellationToken) =>
        runner.RunAsync(SshCommandFactory.CreateDeviceLogin(profile), progress, cancellationToken);
}
