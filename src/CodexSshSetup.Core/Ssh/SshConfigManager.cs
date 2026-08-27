using System.Text;
using System.Text.RegularExpressions;
using CodexSshSetup.Core.Models;
using CodexSshSetup.Core.Validation;

namespace CodexSshSetup.Core.Ssh;

public static class SshConfigManager
{
    public static string ManagedBlockId(string alias) => $"CODEX SSH SETUP {alias}";

    public static string Upsert(string existing, ServerProfile profile, string identityFile)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityFile);

        var errors = ProfileValidator.Validate(profile);
        if (errors.Count != 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        }

        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var marker = ManagedBlockId(profile.Alias);
        var begin = $"# BEGIN {marker}";
        var end = $"# END {marker}";
        var escapedPattern = $@"(?ms)^{Regex.Escape(begin)}\r?\n.*?^{Regex.Escape(end)}(?:\r?\n)?";
        var withoutManagedBlock = Regex.Replace(existing, escapedPattern, string.Empty);

        var builder = new StringBuilder(withoutManagedBlock);
        if (builder.Length > 0 && !EndsWithNewline(builder))
        {
            builder.Append(newline);
        }

        if (builder.Length > 0 && !EndsWithBlankLine(builder, newline))
        {
            builder.Append(newline);
        }

        var normalizedIdentity = identityFile.Replace('\\', '/');
        var identityValue = normalizedIdentity.Any(char.IsWhiteSpace)
            ? $"\"{normalizedIdentity}\""
            : normalizedIdentity;
        builder.Append(begin).Append(newline)
            .Append("Host ").Append(profile.Alias).Append(newline)
            .Append("  HostName ").Append(profile.Host).Append(newline)
            .Append("  User ").Append(profile.UserName).Append(newline)
            .Append("  Port ").Append(profile.Port).Append(newline)
            .Append("  IdentityFile ").Append(identityValue).Append(newline)
            .Append("  IdentitiesOnly yes").Append(newline)
            .Append(end).Append(newline);

        return builder.ToString();
    }

    private static bool EndsWithNewline(StringBuilder builder) =>
        builder.Length > 0 && builder[^1] == '\n';

    private static bool EndsWithBlankLine(StringBuilder builder, string newline) =>
        builder.ToString().EndsWith(newline + newline, StringComparison.Ordinal);
}
