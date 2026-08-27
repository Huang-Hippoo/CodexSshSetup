using System.Text.RegularExpressions;
using CodexSshSetup.Core.Models;

namespace CodexSshSetup.Core.Validation;

public static partial class ProfileValidator
{
    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UserAndAliasPattern();

    public static IReadOnlyList<string> Validate(ServerProfile profile)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Host) ||
            profile.Host.Length > 253 ||
            !HostPattern().IsMatch(profile.Host))
        {
            errors.Add("服务器地址只能包含字母、数字、点、冒号、下划线和连字符。");
        }

        if (string.IsNullOrWhiteSpace(profile.UserName) ||
            profile.UserName.Length > 64 ||
            !UserAndAliasPattern().IsMatch(profile.UserName))
        {
            errors.Add("SSH 用户名只能包含字母、数字、点、下划线和连字符。");
        }

        if (profile.Port is < 1 or > 65535)
        {
            errors.Add("SSH 端口必须在 1 到 65535 之间。");
        }

        if (string.IsNullOrWhiteSpace(profile.Alias) ||
            profile.Alias.Length > 64 ||
            !UserAndAliasPattern().IsMatch(profile.Alias))
        {
            errors.Add("SSH 别名只能包含字母、数字、点、下划线和连字符，且不能含空格。");
        }

        if (profile.RemoteProxyPort is < 1024 or > 65535)
        {
            errors.Add("远端代理端口必须在 1024 到 65535 之间。");
        }

        return errors;
    }
}
