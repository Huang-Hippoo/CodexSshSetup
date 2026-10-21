using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Validation;
using CodexSshSetup.Core.Tunnel;

namespace CodexSshSetup.Core.Commands;

public static class SshCommandFactory
{
    private const string WrapperPath = "$HOME/.local/bin/codex-codex-ssh-setup";

    public static CommandSpec WithConnection(CommandSpec command, ServerProfile profile)
    {
        Validate(profile);
        if (!string.Equals(command.FileName, "ssh.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command.FileName, "scp.exe", StringComparison.OrdinalIgnoreCase)) return command;
        // Keep the alias for its existing identity settings, but never let an old
        // SSH config silently override the host/user/port displayed in the app.
        var options = new[]
        {
            "-o", $"HostName={profile.Host}", "-o", $"User={profile.UserName}", "-o", $"Port={profile.Port}"
        };
        if (command.Arguments.Take(options.Length).SequenceEqual(options)) return command;
        return command with { Arguments = options.Concat(command.Arguments).ToArray() };
    }

    public static CommandSpec CreateKeyGeneration(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        return new CommandSpec("ssh-keygen.exe",
            ["-t", "ed25519", "-f", keyPath, "-N", "", "-C", "codex-ssh-setup"],
            Timeout: TimeSpan.FromSeconds(30));
    }

    public static CommandSpec CreateInteractiveKeyInstall(ServerProfile profile, string publicKey)
    {
        Validate(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        var encodedKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(publicKey.Trim()));
        var remoteCommand = $"umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; " +
            $"key=$(printf '%s' '{encodedKey}' | base64 -d); " +
            "grep -qxF \"$key\" ~/.ssh/authorized_keys || printf '%s\\n' \"$key\" >> ~/.ssh/authorized_keys; " +
            "chmod 700 ~/.ssh; chmod 600 ~/.ssh/authorized_keys";
        return new CommandSpec("ssh.exe",
            ["-p", profile.Port.ToString(), $"{profile.UserName}@{profile.Host}", remoteCommand],
            Interactive: true, Timeout: TimeSpan.FromMinutes(5));
    }

    public static CommandSpec CreateScpUpload(
        ServerProfile profile, string localPath, string remoteFileName)
    {
        Validate(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ValidateFileName(remoteFileName);
        return new CommandSpec("scp.exe", [localPath, $"{profile.Alias}:~/{remoteFileName}"],
            Timeout: TimeSpan.FromMinutes(5));
    }

    public static CommandSpec CreatePasswordlessTest(ServerProfile profile)
    {
        Validate(profile);
        return new CommandSpec("ssh.exe",
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", profile.Alias, "true"],
            Timeout: TimeSpan.FromSeconds(60));
    }

    public static CommandSpec CreateTunnel(ServerProfile profile, int localProxyPort)
    {
        Validate(profile);
        ValidatePort(localProxyPort, nameof(localProxyPort));
        return WithConnection(new CommandSpec("ssh.exe",
        [
            "-T",
            "-R", $"127.0.0.1:{profile.RemoteProxyPort}:127.0.0.1:{localProxyPort}",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=10",
            profile.Alias,
            TunnelHeartbeatProtocol.RemoteCommand
        ]), profile);
    }

    public static CommandSpec CreateRemoteCodexInstall(ServerProfile profile, string packageName)
    {
        Validate(profile);
        if (string.IsNullOrWhiteSpace(packageName) ||
            packageName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            packageName.Contains('/') || packageName.Contains('\\'))
        {
            throw new ArgumentException("包名无效。", nameof(packageName));
        }

        var script = $$"""
            set -eu
            root="$HOME/.local/codex-ssh-setup"
            mkdir -p "$root/npm" "$HOME/.local/bin"
            export PATH="$root/node/bin:$root/npm/bin:$PATH"
            npm install --global --prefix "$HOME/.local/codex-ssh-setup/npm" "$HOME/{{packageName}}"
            cat > "$HOME/.local/bin/codex-codex-ssh-setup" <<'CODEX_WRAPPER'
            #!/bin/sh
            root="$HOME/.local/codex-ssh-setup"
            export PATH="$root/node/bin:$root/npm/bin:$PATH"
            proxy="http://127.0.0.1:17890"
            HTTP_PROXY="$proxy" HTTPS_PROXY="$proxy" http_proxy="$proxy" https_proxy="$proxy" exec "$root/npm/bin/codex" "$@"
            CODEX_WRAPPER
            chmod 700 "$HOME/.local/bin/codex-codex-ssh-setup"
            if [ ! -e "$HOME/.local/bin/codex" ]; then
              ln -s "$HOME/.local/bin/codex-codex-ssh-setup" "$HOME/.local/bin/codex"
            fi
            """;
        return new CommandSpec("ssh.exe", [profile.Alias, "sh -s"], script,
            Timeout: TimeSpan.FromMinutes(5));
    }

    public static CommandSpec CreateDeviceLogin(ServerProfile profile)
    {
        Validate(profile);
        var proxy = $"http://127.0.0.1:{profile.RemoteProxyPort}";
        var remoteCommand = $"env HTTP_PROXY={proxy} HTTPS_PROXY={proxy} http_proxy={proxy} https_proxy={proxy} {WrapperPath} login --device-auth";
        return new CommandSpec("ssh.exe", ["-T", profile.Alias, remoteCommand],
            Interactive: false, Timeout: TimeSpan.FromMinutes(15));
    }

    private static void Validate(ServerProfile profile)
    {
        var errors = ProfileValidator.Validate(profile);
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        }
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw new ArgumentException("文件名无效。", nameof(fileName));
        }
    }
}
