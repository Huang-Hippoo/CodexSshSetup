using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Validation;

namespace CodexSshSetup.Core.Tests;

public sealed class ProfileValidatorTests
{
    [Theory]
    [InlineData("192.0.2.10", "researcher", 22, "node02")]
    [InlineData("gpu.example.edu", "lab-user", 2222, "gpu_node-02")]
    [InlineData("2001:db8::1", "researcher", 22, "ipv6-host")]
    public void Valid_profiles_have_no_errors(string host, string user, int port, string alias)
    {
        var profile = new ServerProfile(host, user, port, alias);

        Assert.Empty(ProfileValidator.Validate(profile));
    }

    [Theory]
    [InlineData("host;shutdown", "user", 22, "node", "服务器地址")]
    [InlineData("host", "user name", 22, "node", "用户名")]
    [InlineData("host", "user", 0, "node", "SSH 端口")]
    [InlineData("host", "user", 65536, "node", "SSH 端口")]
    [InlineData("host", "user", 22, "node 02", "SSH 别名")]
    [InlineData("host", "user", 22, "node$02", "SSH 别名")]
    public void Unsafe_profiles_are_rejected(
        string host, string user, int port, string alias, string expectedField)
    {
        var profile = new ServerProfile(host, user, port, alias);

        var errors = ProfileValidator.Validate(profile);

        Assert.Contains(errors, error => error.Contains(expectedField, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(22)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void Remote_proxy_port_must_be_unprivileged_and_valid(int remotePort)
    {
        var profile = new ServerProfile("host", "user", 22, "node", remotePort);

        Assert.Contains(ProfileValidator.Validate(profile),
            error => error.Contains("远端代理端口", StringComparison.Ordinal));
    }
}
