using CodexSshSetup.Core.Commands;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Parsing;

namespace CodexSshSetup.Core.Tests;

public sealed class CommandAndParsingTests
{
    private static readonly ServerProfile Profile =
        new("192.0.2.10", "researcher", 22, "node02");

    [Fact]
    public void Tunnel_command_binds_only_remote_loopback_and_has_liveness_options()
    {
        var command = SshCommandFactory.CreateTunnel(Profile, 7890);

        Assert.Equal("ssh.exe", command.FileName);
        Assert.Equal(new[]
        {
            "-N", "-T",
            "-R", "127.0.0.1:17890:127.0.0.1:7890",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "node02"
        }, command.Arguments);
        Assert.False(command.Interactive);
    }

    [Fact]
    public void Passwordless_test_is_batch_only()
    {
        var command = SshCommandFactory.CreatePasswordlessTest(Profile);

        Assert.Contains("BatchMode=yes", command.Arguments);
        Assert.Contains("ConnectTimeout=10", command.Arguments);
        Assert.Equal("node02", command.Arguments[^2]);
        Assert.Equal("true", command.Arguments[^1]);
    }

    [Fact]
    public void User_install_script_has_a_private_prefix_and_never_uses_sudo()
    {
        var command = SshCommandFactory.CreateRemoteCodexInstall(Profile, "codex-package.tgz");

        Assert.Equal("ssh.exe", command.FileName);
        Assert.Contains("--prefix \"$HOME/.local/codex-ssh-setup/npm\"", command.StandardInput);
        Assert.Contains("codex-package.tgz", command.StandardInput);
        Assert.DoesNotContain("sudo", command.StandardInput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Device_login_injects_proxy_only_into_that_remote_process()
    {
        var command = SshCommandFactory.CreateDeviceLogin(Profile);

        Assert.Contains("env HTTP_PROXY=http://127.0.0.1:17890", command.Arguments[^1]);
        Assert.Contains("HTTPS_PROXY=http://127.0.0.1:17890", command.Arguments[^1]);
        Assert.Contains("codex-codex-ssh-setup login --device-auth", command.Arguments[^1]);
        Assert.DoesNotContain("export ", command.Arguments[^1]);
    }

    [Theory]
    [InlineData("v20.17.0", 20, 17, 0, true)]
    [InlineData("node v22.3.1", 22, 3, 1, true)]
    [InlineData("v18.20.4", 18, 20, 4, false)]
    public void Node_versions_are_parsed_and_checked(
        string text, int major, int minor, int patch, bool supported)
    {
        Assert.True(ToolVersion.TryParseNode(text, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
        Assert.Equal(supported, ToolVersion.IsNode20OrNewer(text));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Tunnel_command_rejects_invalid_local_proxy_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SshCommandFactory.CreateTunnel(Profile, port));
    }

    [Fact]
    public void Key_generation_uses_ed25519_and_an_empty_passphrase_as_separate_arguments()
    {
        var command = SshCommandFactory.CreateKeyGeneration("C:/Users/me/.ssh/codex_node02_ed25519");

        Assert.Equal("ssh-keygen.exe", command.FileName);
        Assert.Equal(new[]
        {
            "-t", "ed25519", "-f", "C:/Users/me/.ssh/codex_node02_ed25519",
            "-N", "", "-C", "codex-ssh-setup"
        }, command.Arguments);
    }

    [Fact]
    public void Key_install_is_interactive_and_public_key_is_encoded()
    {
        const string publicKey = "ssh-ed25519 AAAAC3NzaExample codex-ssh-setup";

        var command = SshCommandFactory.CreateInteractiveKeyInstall(Profile, publicKey);

        Assert.True(command.Interactive);
        Assert.Equal("researcher@192.0.2.10", command.Arguments[^2]);
        Assert.DoesNotContain(publicKey, command.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("authorized_keys", command.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("chmod 700", command.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("chmod 600", command.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Scp_upload_targets_the_profile_alias()
    {
        var command = SshCommandFactory.CreateScpUpload(Profile, "C:/Temp/codex.tgz", "codex.tgz");

        Assert.Equal("scp.exe", command.FileName);
        Assert.Equal(new[] { "C:/Temp/codex.tgz", "node02:~/codex.tgz" }, command.Arguments);
    }
}
