using System.Text.Json;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.Core.Tests;

public sealed class AppConfigSerializationTests
{
    [Fact]
    public void Config_round_trips_only_non_sensitive_settings()
    {
        var original = new AppConfig
        {
            Profile = new ServerProfile("gpu.example.edu", "researcher", 22, "gpu02"),
            LocalProxyPort = 7890,
            CloseBehavior = CloseBehavior.Ask
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppConfig>(json);

        Assert.Equal(original, restored);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicecode", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privatekey", json, StringComparison.OrdinalIgnoreCase);
    }
}
