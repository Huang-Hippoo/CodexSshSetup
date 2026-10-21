using System.Text.RegularExpressions;
using CodexSshSetup.Core.Validation;

namespace CodexSshSetup.Core.Models;

public sealed record SavedConnection(ServerProfile Profile, int? LocalProxyPort = 7890);

public sealed record ConnectionCatalog
{
    public int Version { get; init; } = 1;
    public string? SelectedAlias { get; init; }
    public List<SavedConnection> Connections { get; init; } = [];
}

public static class ConnectionHistory
{
    public static ConnectionCatalog Import(AppConfig legacy, string sshConfig)
    {
        var connections = new List<SavedConnection>();
        void Add(ServerProfile profile)
        {
            if (ProfileValidator.Validate(profile).Count == 0 &&
                !connections.Any(item => string.Equals(item.Profile.Alias, profile.Alias, StringComparison.OrdinalIgnoreCase)))
                connections.Add(new SavedConnection(profile, legacy.LocalProxyPort ?? 7890));
        }
        if (legacy.Profile is { } current) Add(current);

        // Import only complete blocks written by this app. Never evaluate SSH
        // includes, wildcards, Match conditions, shell commands, or arbitrary text.
        foreach (Match match in Regex.Matches(sshConfig,
                     @"(?m)^# BEGIN CODEX SSH SETUP (?<alias>[A-Za-z0-9._-]+)\r?\n(?<body>[\s\S]*?)^# END CODEX SSH SETUP \k<alias>\r?$"))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in match.Groups["body"].Value.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) values.TryAdd(parts[0], parts[1].Trim().Trim('"'));
            }
            var alias = match.Groups["alias"].Value;
            if (!values.TryGetValue("Host", out var hostAlias) || hostAlias != alias ||
                !values.TryGetValue("HostName", out var host) || !values.TryGetValue("User", out var user)) continue;
            var port = values.TryGetValue("Port", out var text) ? (int.TryParse(text, out var parsed) ? parsed : 0) : 22;
            Add(new ServerProfile(host, user, port, alias));
        }
        return new ConnectionCatalog
        {
            Connections = connections,
            SelectedAlias = connections.FirstOrDefault()?.Profile.Alias
        };
    }

    public static bool SharesTunnelEndpoint(ServerProfile left, ServerProfile right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port && left.RemoteProxyPort == right.RemoteProxyPort;
}
