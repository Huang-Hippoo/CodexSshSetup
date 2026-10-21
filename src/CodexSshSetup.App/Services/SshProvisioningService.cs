using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.App.Models;
using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Parsing;
using CodexSshSetup.Core.Ssh;

namespace CodexSshSetup.App.Services;

public sealed class SshProvisioningService(IProcessRunner runner, FileLog log)
{
    private static readonly HttpClient Downloads = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim SshConfigGate = new(1, 1);
    public async Task TestPortAsync(ServerProfile profile, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await client.ConnectAsync(profile.Host, profile.Port, timeout.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("SSH 端口连接超时（等待上限 8 秒）。请检查服务器地址、端口和网络连接。", exception);
        }
    }

    public string GetKeyPath(ServerProfile profile)
    {
        var sshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        return Path.Combine(sshDirectory, $"codex_ssh_setup_{profile.Alias}_ed25519");
    }

    public async Task<string> EnsureKeyAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var keyPath = GetKeyPath(profile);
        var publicKeyPath = keyPath + ".pub";
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        if (File.Exists(keyPath) && File.Exists(publicKeyPath))
        {
            progress?.Report("复用现有专用 SSH 密钥。");
            return keyPath;
        }

        if (File.Exists(keyPath) || File.Exists(publicKeyPath))
        {
            throw new InvalidOperationException("SSH 密钥文件不完整；请备份并删除残缺的专用 key 后重试。");
        }

        var result = await runner.RunAsync(
            SshCommandFactory.CreateKeyGeneration(keyPath), progress, cancellationToken);
        EnsureSuccess(result, "生成 SSH 密钥");
        return keyPath;
    }

    public async Task InstallPublicKeyAsync(
        ServerProfile profile, string keyPath, CancellationToken cancellationToken)
    {
        var publicKey = await File.ReadAllTextAsync(keyPath + ".pub", cancellationToken);
        var result = await runner.RunAsync(
            SshCommandFactory.CreateInteractiveKeyInstall(profile, publicKey), null, cancellationToken);
        EnsureSuccess(result, "安装 SSH 公钥");
    }

    public async Task UpdateSshConfigAsync(
        ServerProfile profile, string keyPath, CancellationToken cancellationToken)
    {
        await SshConfigGate.WaitAsync(cancellationToken);
        try
        {
        var sshDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        Directory.CreateDirectory(sshDirectory);
        var configPath = Path.Combine(sshDirectory, "config");
        var existing = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, cancellationToken)
            : string.Empty;
        var updated = SshConfigManager.Upsert(existing, profile, keyPath);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return;
        }

        if (File.Exists(configPath))
        {
            File.Copy(configPath, configPath + ".codex-ssh-setup.bak", overwrite: true);
        }
        var tempPath = configPath + ".codex-ssh-setup.tmp";
        await File.WriteAllTextAsync(tempPath, updated, cancellationToken);
        File.Move(tempPath, configPath, overwrite: true);
        }
        finally { SshConfigGate.Release(); }
    }

    public async Task TestPasswordlessAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("正在测试免密 SSH 登录（最多 60 秒）…");
        var result = await runner.RunAsync(
            SshCommandFactory.CreatePasswordlessTest(profile), progress, cancellationToken);
        EnsureSuccess(result, "免密 SSH 测试");
        progress?.Report("免密 SSH 登录测试通过。");
    }

    public async Task<RemoteToolInfo> InspectToolsAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report("正在检测远端 Node/npm/Codex（最多 120 秒；首次启动可能较慢）…");
        const string remote = "PATH=\"$HOME/.local/codex-ssh-setup/node/bin:$HOME/.local/codex-ssh-setup/npm/bin:$HOME/.local/bin:$PATH\"; " +
            "printf 'NODE=%s\\n' \"$(node -v 2>/dev/null || echo missing)\"; " +
            "printf 'NPM=%s\\n' \"$(npm -v 2>/dev/null || echo missing)\"; " +
            "printf 'CODEX_PATH=%s\\n' \"$(command -v codex 2>/dev/null || echo missing)\"; " +
            "printf 'CODEX_VERSION=%s\\n' \"$(codex --version 2>/dev/null || echo missing)\"; " +
            "if [ -x \"$HOME/.local/bin/codex-codex-ssh-setup\" ]; then echo WRAPPER=yes; else echo WRAPPER=no; fi";
        var result = await runner.RunAsync(
            new CommandSpec("ssh.exe", [profile.Alias, remote], Timeout: TimeSpan.FromSeconds(120)),
            progress, cancellationToken);
        EnsureSuccess(result, "检测远端工具");
        var values = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        return new RemoteToolInfo(
            values.GetValueOrDefault("NODE", "missing"),
            values.GetValueOrDefault("NPM", "missing"),
            values.GetValueOrDefault("CODEX_PATH", "missing"),
            values.GetValueOrDefault("CODEX_VERSION", "missing"),
            values.GetValueOrDefault("WRAPPER", "no") == "yes");
    }

    public async Task EnsureNodeAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var info = await InspectToolsAsync(profile, progress, cancellationToken);
        if (info.HasNode20OrNewer && info.HasNpm)
        {
            progress?.Report($"复用远端 {info.NodeVersion}。");
            return;
        }

        var condaCheck = await runner.RunAsync(
            new CommandSpec("ssh.exe", [profile.Alias, "command -v conda"], Timeout: TimeSpan.FromSeconds(20)),
            progress, cancellationToken);
        if (condaCheck.Success)
        {
            const string script = "set -eu\n" +
            "  conda create -y -p \"$HOME/.local/codex-ssh-setup/conda\" 'nodejs>=20'\n" +
            "  mkdir -p \"$HOME/.local/codex-ssh-setup/node/bin\"\n" +
            "  ln -sf \"$HOME/.local/codex-ssh-setup/conda/bin/node\" \"$HOME/.local/codex-ssh-setup/node/bin/node\"\n" +
            "  ln -sf \"$HOME/.local/codex-ssh-setup/conda/bin/npm\" \"$HOME/.local/codex-ssh-setup/node/bin/npm\"\n";
            var result = await runner.RunAsync(
                new CommandSpec("ssh.exe", [profile.Alias, "sh -s"], script, Timeout: TimeSpan.FromMinutes(15)),
                progress, cancellationToken);
            EnsureSuccess(result, "通过 conda 安装用户级 Node");
            return;
        }

        await InstallUploadedNodeAsync(profile, progress, cancellationToken);
    }

    private async Task InstallUploadedNodeAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var architectureResult = await runner.RunAsync(
            new CommandSpec("ssh.exe", [profile.Alias, "uname -m"], Timeout: TimeSpan.FromSeconds(20)),
            progress, cancellationToken);
        EnsureSuccess(architectureResult, "检测 Linux 架构");
        var architecture = architectureResult.StandardOutput.Trim();
        progress?.Report($"远端架构：{architecture}；从 Windows 下载 Node 20 LTS 包。");

        var manifest = await Downloads.GetStringAsync(
            "https://nodejs.org/dist/latest-v20.x/SHASUMS256.txt", cancellationToken);
        if (!NodeManifest.TryFindPackage(manifest, architecture, out var package))
        {
            throw new InvalidOperationException($"不支持的 Linux 架构：{architecture}。");
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "CodexSshSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var archivePath = Path.Combine(tempDirectory, package.FileName);
        try
        {
            await using (var source = await Downloads.GetStreamAsync(
                             $"https://nodejs.org/dist/latest-v20.x/{package.FileName}", cancellationToken))
            await using (var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            await using (var stream = File.OpenRead(archivePath))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(actual, package.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Node 下载包 SHA-256 校验失败，已拒绝上传。");
                }
            }

            var upload = await runner.RunAsync(
                SshCommandFactory.CreateScpUpload(profile, archivePath, package.FileName),
                progress, cancellationToken);
            EnsureSuccess(upload, "上传 Node 安装包");

            var script = $$"""
                set -eu
                root="$HOME/.local/codex-ssh-setup"
                staging="$root/node.new"
                rm -rf "$staging"
                mkdir -p "$staging"
                tar -xJf "$HOME/{{package.FileName}}" -C "$staging" --strip-components=1
                rm -rf "$root/node"
                mv "$staging" "$root/node"
                rm -f "$HOME/{{package.FileName}}"
                "$root/node/bin/node" -v
                "$root/node/bin/npm" -v
                """;
            var install = await runner.RunAsync(
                new CommandSpec("ssh.exe", [profile.Alias, "sh -s"], script, Timeout: TimeSpan.FromMinutes(10)),
                progress, cancellationToken);
            EnsureSuccess(install, "安装上传的用户级 Node");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException exception)
            {
                await log.WriteAsync($"Node 临时目录清理失败：{exception.Message}");
            }
        }
    }

    public async Task InstallCodexAsync(
        ServerProfile profile, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "CodexSshSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var pack = await runner.RunAsync(
                new CommandSpec("npm.cmd", ["pack", "@openai/codex", "--pack-destination", tempDirectory],
                    Timeout: TimeSpan.FromMinutes(5)), progress, cancellationToken);
            EnsureSuccess(pack, "在 Windows 打包 Codex");
            var packagePath = Directory.GetFiles(tempDirectory, "*.tgz").SingleOrDefault()
                ?? throw new InvalidOperationException("npm pack 未产生 tgz 文件。");
            var packageName = Path.GetFileName(packagePath);
            var upload = await runner.RunAsync(
                SshCommandFactory.CreateScpUpload(profile, packagePath, packageName),
                progress, cancellationToken);
            EnsureSuccess(upload, "上传 Codex 安装包");
            var install = await runner.RunAsync(
                SshCommandFactory.CreateRemoteCodexInstall(profile, packageName),
                progress, cancellationToken);
            EnsureSuccess(install, "安装用户级 Codex");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch (IOException exception)
            {
                await log.WriteAsync($"临时目录清理失败：{exception.Message}");
            }
        }
    }

    private static void EnsureSuccess(ProcessResult result, string step)
    {
        if (result.Success)
        {
            return;
        }

        var raw = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        throw new InvalidOperationException($"{step}失败（退出码 {result.ExitCode}）：{MapError(raw)}");
    }

    public static string MapError(string raw)
    {
        if (raw.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return "连接被拒绝，请检查 Host、端口和 sshd。";
        if (raw.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "SSH 认证失败，请检查账号、密码/密钥和服务端策略。";
        if (raw.Contains("remote port forwarding failed", StringComparison.OrdinalIgnoreCase))
            return "远端 17890 端口被占用，或服务器禁止 TCP 转发。";
        if (raw.Contains("CODEX_SETUP_NODE_REQUIRED", StringComparison.Ordinal))
            return "远端缺少 Node 20+ 且没有可用 conda；请先安装用户级 Node 后重试。";
        if (raw.Contains("command not found", StringComparison.OrdinalIgnoreCase))
            return "远端命令不存在；请查看原始日志确认缺少 Node、npm 或 Codex。";
        return string.IsNullOrWhiteSpace(raw) ? "未知错误，请查看日志。" : raw.Trim();
    }
}
