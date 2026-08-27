using CodexSshSetup.App.Models;

namespace CodexSshSetup.Core.Tests;

public sealed class RemoteToolInfoTests
{
    [Fact]
    public void Existing_codex_without_managed_wrapper_is_not_ready()
    {
        var tools = new RemoteToolInfo("v20.17.0", "10.8.2", "/usr/bin/codex", "codex-cli 0.149.1", false);

        Assert.True(tools.HasCodex);
        Assert.False(tools.IsReadyForDesktop);
    }
}
