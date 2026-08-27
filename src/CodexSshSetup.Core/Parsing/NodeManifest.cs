using System.Text.RegularExpressions;

namespace CodexSshSetup.Core.Parsing;

public sealed record NodePackage(string FileName, string Sha256);

public static partial class NodeManifest
{
    [GeneratedRegex(@"^([a-fA-F0-9]{64})\s+(node-v20[^\s]*-linux-(?:x64|arm64)\.tar\.xz)$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();

    public static bool TryFindPackage(string manifest, string architecture, out NodePackage package)
    {
        var suffix = architecture.ToLowerInvariant() switch
        {
            "x86_64" or "amd64" => "linux-x64.tar.xz",
            "aarch64" or "arm64" => "linux-arm64.tar.xz",
            _ => null
        };

        if (suffix is not null)
        {
            foreach (var line in (manifest ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var match = EntryPattern().Match(line.Trim());
                if (match.Success && match.Groups[2].Value.EndsWith(suffix, StringComparison.Ordinal))
                {
                    package = new NodePackage(match.Groups[2].Value, match.Groups[1].Value.ToLowerInvariant());
                    return true;
                }
            }
        }

        package = new NodePackage(string.Empty, string.Empty);
        return false;
    }
}
