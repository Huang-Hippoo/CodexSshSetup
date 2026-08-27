using CodexSshSetup.Core.Logging;

namespace CodexSshSetup.Core.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_removes_authorization_tokens_and_query_secrets()
    {
        const string input = "Authorization: Bearer sk-secret-value\n" +
            "https://example.test/callback?code=abc123&state=keep\n" +
            "Connection refused";

        var result = SecretRedactor.Redact(input);

        Assert.DoesNotContain("sk-secret-value", result, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", result, StringComparison.Ordinal);
        Assert.Contains("Authorization: [REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("code=[REDACTED]", result, StringComparison.Ordinal);
        Assert.Contains("Connection refused", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Your device code is ABCD-EFGH")]
    [InlineData("Enter code: WXYZ-1234")]
    public void Redact_removes_device_codes_from_persisted_logs(string input)
    {
        var result = SecretRedactor.Redact(input);

        Assert.DoesNotContain("ABCD-EFGH", result, StringComparison.Ordinal);
        Assert.DoesNotContain("WXYZ-1234", result, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result, StringComparison.Ordinal);
    }
}
