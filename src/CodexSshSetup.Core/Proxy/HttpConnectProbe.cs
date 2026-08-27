using System.Text.RegularExpressions;

namespace CodexSshSetup.Core.Proxy;

public sealed record ProxyProbeResult(bool Success, int? StatusCode, string Message);

public static partial class HttpConnectProbe
{
    [GeneratedRegex(@"^HTTP/\d(?:\.\d)?\s+(\d{3})(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatusLinePattern();

    public static ProxyProbeResult ParseResponse(string response)
    {
        var firstLine = (response ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var match = StatusLinePattern().Match(firstLine);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var statusCode))
        {
            return new ProxyProbeResult(false, null, "无法识别代理响应。");
        }

        if (statusCode is >= 200 and <= 299)
        {
            return new ProxyProbeResult(true, statusCode, "HTTPS CONNECT 已建立。");
        }

        if (statusCode == 407)
        {
            return new ProxyProbeResult(false, statusCode, "代理要求认证。");
        }

        return new ProxyProbeResult(false, statusCode, $"代理拒绝 CONNECT（HTTP {statusCode}）。");
    }
}
