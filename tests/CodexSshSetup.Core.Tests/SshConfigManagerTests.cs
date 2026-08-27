using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Ssh;

namespace CodexSshSetup.Core.Tests;

public sealed class SshConfigManagerTests
{
    private static readonly ServerProfile Profile =
        new("192.0.2.10", "researcher", 2222, "node02");

    [Fact]
    public void Upsert_adds_a_managed_host_without_changing_unrelated_entries()
    {
        const string existing = "# Personal host\nHost github.com\n  User git\n";

        var result = SshConfigManager.Upsert(existing, Profile,
            @"C:\Users\me\.ssh\codex_node02_ed25519");

        Assert.StartsWith(existing, result, StringComparison.Ordinal);
        Assert.Contains("# BEGIN CODEX SSH SETUP node02", result, StringComparison.Ordinal);
        Assert.Contains("  HostName 192.0.2.10", result, StringComparison.Ordinal);
        Assert.Contains("  User researcher", result, StringComparison.Ordinal);
        Assert.Contains("  Port 2222", result, StringComparison.Ordinal);
        Assert.Contains("  IdentityFile C:/Users/me/.ssh/codex_node02_ed25519", result, StringComparison.Ordinal);
        Assert.Contains("  IdentitiesOnly yes", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_replaces_only_the_existing_block_for_the_same_alias()
    {
        const string existing = "Host keep\n  HostName keep.example\n\n" +
            "# BEGIN CODEX SSH SETUP node02\nHost node02\n  HostName old\n# END CODEX SSH SETUP node02\n";
        var updated = Profile with { Host = "new.example.edu", Port = 22 };

        var result = SshConfigManager.Upsert(existing, updated, "C:/keys/node02");

        Assert.Contains("Host keep\n  HostName keep.example", result, StringComparison.Ordinal);
        Assert.DoesNotContain("HostName old", result, StringComparison.Ordinal);
        Assert.Equal(1, Count(result, "# BEGIN CODEX SSH SETUP node02"));
        Assert.Contains("HostName new.example.edu", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_preserves_crlf_style()
    {
        const string existing = "Host keep\r\n  User me\r\n";

        var result = SshConfigManager.Upsert(existing, Profile, "C:/keys/node02");

        Assert.DoesNotContain("\n", result.Replace("\r\n", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_file_with_spaces_is_quoted_for_openssh()
    {
        var result = SshConfigManager.Upsert(string.Empty, Profile,
            @"C:\Users\Lab User\.ssh\codex_node02_ed25519");

        Assert.Contains("IdentityFile \"C:/Users/Lab User/.ssh/codex_node02_ed25519\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_rejects_an_alias_that_could_capture_other_hosts()
    {
        var unsafeProfile = Profile with { Alias = "*" };

        Assert.Throws<ArgumentException>(() =>
            SshConfigManager.Upsert(string.Empty, unsafeProfile, "C:/keys/key"));
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;
}
