using System.Text.RegularExpressions;

namespace CodexSshSetup.Core.Logging;

public static partial class SecretRedactor
{
    [GeneratedRegex(@"(?im)^Authorization:\s*[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+\S+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)([?&](?:code|token|access_token|id_token)=)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex QuerySecretPattern();

    [GeneratedRegex(@"(?i)((?:device\s+code\s+(?:is|:)|enter\s+code:)\s*)[A-Z0-9]{4,}(?:-[A-Z0-9]{4,})+", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCodePattern();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = AuthorizationPattern().Replace(text, "Authorization: [REDACTED]");
        result = BearerPattern().Replace(result, "Bearer [REDACTED]");
        result = QuerySecretPattern().Replace(result, "$1[REDACTED]");
        return DeviceCodePattern().Replace(result, "$1[REDACTED]");
    }
}
