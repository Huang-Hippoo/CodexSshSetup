using System.Text.Json;
using CodexSshSetup.App.Infrastructure;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.Core.Tests;

public sealed class ConnectionCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexSshSetupTests", Guid.NewGuid().ToString("N"));
    private const string ManagedSsh = """
        Host unrelated
          HostName unrelated.example
          User guest
        # BEGIN CODEX SSH SETUP node01
        Host node01
          HostName 192.0.2.10
          User researcher
          Port 22
        # END CODEX SSH SETUP node01
        # BEGIN CODEX SSH SETUP lab-container
        Host lab-container
          HostName 192.0.2.20
          User labuser
          Port 6115
        # END CODEX SSH SETUP lab-container
        """;

    [Fact]
    public void Legacy_current_node_and_managed_history_are_imported_without_unrelated_hosts()
    {
        var profile = new ServerProfile("192.0.2.10", "researcher", 22, "node01");
        var catalog = ConnectionHistory.Import(new AppConfig { Profile = profile, LocalProxyPort = 7890 }, ManagedSsh);
        Assert.Equal(2, catalog.Connections.Count);
        Assert.Equal("node01", catalog.SelectedAlias);
        Assert.All(catalog.Connections, item => Assert.Equal(17890, item.Profile.RemoteProxyPort));
        Assert.Contains(catalog.Connections, item => item.Profile.Port == 6115);
        Assert.DoesNotContain(catalog.Connections, item => item.Profile.Alias == "unrelated");
    }

    [Fact]
    public void Unsafe_or_incomplete_managed_blocks_are_not_imported()
    {
        var config = """
            # BEGIN CODEX SSH SETUP broken
            Host broken
              HostName example.com
              User user
              Port not-a-port
            # END CODEX SSH SETUP broken
            # BEGIN CODEX SSH SETUP missing
            Host missing
              HostName example.com
            # END CODEX SSH SETUP missing
            """;
        Assert.Empty(ConnectionHistory.Import(new AppConfig(), config).Connections);
    }

    [Fact]
    public void Same_host_different_ssh_ports_are_distinct_but_different_aliases_or_users_share_port()
    {
        var node = new ServerProfile("host", "user", 22, "first");
        Assert.True(ConnectionHistory.SharesTunnelEndpoint(node, node with { Alias = "other", UserName = "another-user" }));
        Assert.False(ConnectionHistory.SharesTunnelEndpoint(node, node with { Port = 6115 }));
        Assert.False(ConnectionHistory.SharesTunnelEndpoint(node, node with { Host = "another-server" }));
    }

    [Fact]
    public async Task Migration_preserves_legacy_json_and_multi_node_selection_survives_restart()
    {
        Directory.CreateDirectory(_root);
        var legacyJson = """{"Profile":{"Host":"192.0.2.10","UserName":"researcher","Port":22,"Alias":"node01","RemoteProxyPort":17890},"LocalProxyPort":7890} """;
        var legacyPath = Path.Combine(_root, "config.json");
        await File.WriteAllTextAsync(legacyPath, legacyJson);
        var store = new ConnectionCatalogStore(_root);
        var catalog = await store.LoadAsync(ManagedSsh, CancellationToken.None);
        await store.SaveAsync(catalog with { SelectedAlias = "lab-container" }, CancellationToken.None);

        var reloaded = await new ConnectionCatalogStore(_root).LoadAsync("", CancellationToken.None);
        Assert.Equal("lab-container", reloaded.SelectedAlias);
        Assert.Equal(catalog.Connections, reloaded.Connections);
        Assert.Equal(legacyJson, await File.ReadAllTextAsync(legacyPath));
    }

    [Fact]
    public async Task Concurrent_saves_leave_valid_complete_json_and_previous_version_backup()
    {
        var store = new ConnectionCatalogStore(_root);
        var initial = ConnectionHistory.Import(new AppConfig(), ManagedSsh);
        await store.SaveAsync(initial, CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(1, 12).Select(index => store.SaveAsync(initial with { SelectedAlias = $"node{index}" }, CancellationToken.None)));
        var restored = await store.LoadAsync("", CancellationToken.None);
        Assert.Equal(2, restored.Connections.Count);
        Assert.NotNull(JsonSerializer.Deserialize<ConnectionCatalog>(await File.ReadAllTextAsync(Path.Combine(_root, "connections.json.bak"))));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Corrupt_catalog_is_preserved_and_does_not_silently_replace_saved_nodes()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "connections.json");
        await File.WriteAllTextAsync(path, "{ broken json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new ConnectionCatalogStore(_root).LoadAsync(ManagedSsh, CancellationToken.None));
        Assert.Equal("{ broken json", await File.ReadAllTextAsync(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
