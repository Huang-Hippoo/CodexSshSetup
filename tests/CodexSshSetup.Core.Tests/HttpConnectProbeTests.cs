using CodexSshSetup.Core.Proxy;

namespace CodexSshSetup.Core.Tests;

public sealed class HttpConnectProbeTests
{
    [Theory]
    [InlineData("HTTP/1.1 200 Connection established\r\n\r\n", 200)]
    [InlineData("HTTP/1.0 204 No Content\r\n\r\n", 204)]
    public void Two_hundred_responses_mean_connect_succeeded(string response, int code)
    {
        var result = HttpConnectProbe.ParseResponse(response);

        Assert.True(result.Success);
        Assert.Equal(code, result.StatusCode);
    }

    [Fact]
    public void Proxy_authentication_is_reported_separately()
    {
        var result = HttpConnectProbe.ParseResponse("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");

        Assert.False(result.Success);
        Assert.Equal(407, result.StatusCode);
        Assert.Contains("认证", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cloudflare_403_is_not_misclassified_as_connect_success()
    {
        var result = HttpConnectProbe.ParseResponse("HTTP/1.1 403 Forbidden\r\n\r\n");

        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public void Malformed_proxy_response_has_no_status_code()
    {
        var result = HttpConnectProbe.ParseResponse("not http");

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("无法识别", result.Message, StringComparison.Ordinal);
    }
}
