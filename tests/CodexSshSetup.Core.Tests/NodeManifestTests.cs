using CodexSshSetup.Core.Parsing;

namespace CodexSshSetup.Core.Tests;

public sealed class NodeManifestTests
{
    private const string Manifest = """
        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  node-v20.19.5-linux-arm64.tar.xz
        bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb  node-v20.19.5-linux-x64.tar.xz
        cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc  node-v20.19.5-win-x64.zip
        """;

    [Theory]
    [InlineData("x86_64", "node-v20.19.5-linux-x64.tar.xz", "bbbbbbbb")]
    [InlineData("amd64", "node-v20.19.5-linux-x64.tar.xz", "bbbbbbbb")]
    [InlineData("aarch64", "node-v20.19.5-linux-arm64.tar.xz", "aaaaaaaa")]
    [InlineData("arm64", "node-v20.19.5-linux-arm64.tar.xz", "aaaaaaaa")]
    public void Manifest_selects_the_linux_archive_for_remote_architecture(
        string architecture, string expectedFile, string checksumPrefix)
    {
        Assert.True(NodeManifest.TryFindPackage(Manifest, architecture, out var package));
        Assert.Equal(expectedFile, package.FileName);
        Assert.StartsWith(checksumPrefix, package.Sha256, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_architecture_is_rejected()
    {
        Assert.False(NodeManifest.TryFindPackage(Manifest, "riscv64", out _));
    }
}
