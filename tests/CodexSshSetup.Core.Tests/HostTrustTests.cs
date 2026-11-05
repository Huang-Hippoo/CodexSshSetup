using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Ssh;

namespace CodexSshSetup.Core.Tests;

public class HostTrustTests
{
    [Fact]
    public void Migration_collects_multiple_known_nodes_on_same_gateway_only()
    {
        var text = "[192.0.2.1]:6110 ssh-ed25519 YWJj\n" +
            "[192.0.2.1]:6120 ssh-ed25519 ZGVm\n" +
            "[192.0.2.10]:6110 ssh-ed25519 Z2hp\n" +
            "[192.0.2.1]:invalid ssh-ed25519 Z2hp\n" +
            "@revoked [192.0.2.1]:6110 ssh-ed25519 Z2hp\n" +
            "*.112.1 ssh-ed25519 Z2hp\n";
        Assert.Equal(new[] { "ssh-ed25519 YWJj", "ssh-ed25519 ZGVm" }, HostTrust.Keys(text, "192.0.2.1", true));
    }

    [Fact]
    public void Unknown_key_is_not_part_of_the_trusted_set()
    {
        var trusted = HostTrust.Keys("gateway ssh-ed25519 YWJj\n", "gateway", false);
        var scanned = HostTrust.Keys("[192.0.2.1]:2222 ssh-ed25519 ZGVm\n", "192.0.2.1", true);
        Assert.Empty(scanned.Intersect(trusted));
    }

    [Fact]
    public void Changing_port_keeps_identity_and_updates_connection_port()
    {
        var profile = new ServerProfile("192.0.2.1", "user", 6110, "my-node");
        var before = SshConfigManager.Upsert("", profile, "C:/keys/node");
        var after = SshConfigManager.Upsert(before, profile with { Port = 6120 }, "C:/keys/node");
        Assert.Contains("Port 6120", after);
        Assert.DoesNotContain("Port 6110", after);
        Assert.Contains("HostKeyAlias " + HostTrust.Name(profile.Host), before);
        Assert.Contains("HostKeyAlias " + HostTrust.Name(profile.Host), after);
        Assert.Contains("StrictHostKeyChecking yes", after);
        Assert.Contains("UserKnownHostsFile \"C:/keys/node.known_hosts\"", after);
    }

    [Fact]
    public void Invalid_keys_and_comments_are_ignored()
    {
        Assert.Empty(HostTrust.Keys("gateway ssh-ed25519 invalid!\n# comment\n", "gateway", false));
        Assert.NotEqual(HostTrust.Name("192.0.2.1"), HostTrust.Name("192.0.2.2"));
        Assert.Equal("SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0", HostTrust.Fingerprint("ssh-ed25519 YWJj"));
    }
}
