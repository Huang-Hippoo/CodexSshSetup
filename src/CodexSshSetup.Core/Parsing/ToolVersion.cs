using System.Text.RegularExpressions;

namespace CodexSshSetup.Core.Parsing;

public static partial class ToolVersion
{
    [GeneratedRegex(@"(?:^|\s)v?(\d+)\.(\d+)\.(\d+)(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool TryParseNode(string text, out Version version)
    {
        var match = VersionPattern().Match(text ?? string.Empty);
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, out var major) &&
            int.TryParse(match.Groups[2].Value, out var minor) &&
            int.TryParse(match.Groups[3].Value, out var patch))
        {
            version = new Version(major, minor, patch);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsNode20OrNewer(string text) =>
        TryParseNode(text, out var version) && version.Major >= 20;
}
