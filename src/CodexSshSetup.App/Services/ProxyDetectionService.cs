using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using CodexSshSetup.Core.Proxy;
using CodexSshSetup.Core.Parsing;

namespace CodexSshSetup.App.Services;

public sealed record ProxyDetectionResult(
    bool ProcessRunning,
    int? Port,
    bool ConnectSucceeded,
    string Message);

public sealed class ProxyDetectionService
{
    private static readonly int[] CommonPorts = [7890, 7897, 10809, 8080, 1080, 7891];
    private static readonly string[] ProcessNames = ["iKuuuVPNCore", "mihomo", "clash", "clash-meta", "sing-box", "v2ray", "xray"];
    private readonly Func<int, CancellationToken, Task<ProxyProbeResult>> _probe;

    public ProxyDetectionService(Func<int, CancellationToken, Task<ProxyProbeResult>>? probe = null) => _probe = probe ?? ProbeAsync;

    public async Task<ProxyDetectionResult> DetectAsync(
        int? preferredPort, CancellationToken cancellationToken)
    {
        if (preferredPort is > 0 and <= 65535)
        {
            var preferred = await _probe(preferredPort.Value, cancellationToken);
            if (preferred.Success) return new ProxyDetectionResult(true, preferredPort, true, $"已验证本机代理 127.0.0.1:{preferredPort}。");
        }
        var processes = ProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
        try
        {
            var candidates = new List<int>();
            foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy" })
                candidates.AddRange(ParseLoopbackPorts(Environment.GetEnvironmentVariable(name)));
            try
            {
                var settings = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
                if (Microsoft.Win32.Registry.GetValue(settings, "ProxyEnable", 0) is int enabled && enabled != 0)
                    candidates.AddRange(ParseLoopbackPorts(Microsoft.Win32.Registry.GetValue(settings, "ProxyServer", null) as string));
            }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException) { }
            if (processes.Length > 0)
                candidates.AddRange(await FindProcessPortsAsync(processes.Select(process => process.Id).ToArray(), cancellationToken));
            candidates.AddRange(CommonPorts);
            var ports = candidates.Where(port => port != preferredPort).Distinct().Take(24).ToArray();
            var probes = await Task.WhenAll(ports.Select(port => _probe(port, cancellationToken)));
            for (var index = 0; index < ports.Length; index++)
            {
                if (probes[index].Success)
                {
                    return new ProxyDetectionResult(processes.Length > 0, ports[index], true, $"自动检测到本机 HTTP/mixed 代理 127.0.0.1:{ports[index]}，HTTPS CONNECT 可用。");
                }
            }

            return new ProxyDetectionResult(processes.Length > 0, null, false,
                "未找到可用的本机 HTTP/mixed 代理。请启动代理程序并启用 HTTP/mixed 监听，也可输入实际端口重试。");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public static IReadOnlyList<int> ParseLoopbackPorts(string? settings)
    {
        var ports = new List<int>();
        foreach (var entry in (settings ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = entry.StartsWith("http=", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("https=", StringComparison.OrdinalIgnoreCase)
                ? entry[(entry.IndexOf('=') + 1)..] : entry;
            if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port is > 0 and <= 65535)
                ports.Add(uri.Port);
        }
        return ports.Distinct().ToArray();
    }

    private static async Task<IReadOnlyList<int>> FindProcessPortsAsync(
        IReadOnlyCollection<int> processIds, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("netstat.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-ano");
        info.ArgumentList.Add("-p");
        info.ArgumentList.Add("tcp");
        using var process = Process.Start(info);
        if (process is null) return [];
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return NetstatParser.FindListeningPorts(await outputTask, processIds);
    }

    public static async Task<ProxyProbeResult> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            await using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes(
                "CONNECT auth.openai.com:443 HTTP/1.1\r\nHost: auth.openai.com:443\r\nProxy-Connection: keep-alive\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token);
            var buffer = new byte[4096];
            var count = await stream.ReadAsync(buffer, timeout.Token);
            return HttpConnectProbe.ParseResponse(Encoding.ASCII.GetString(buffer, 0, count));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProxyProbeResult(false, null, "代理 CONNECT 超时。");
        }
        catch (SocketException exception)
        {
            return new ProxyProbeResult(false, null, $"代理端口连接失败：{exception.SocketErrorCode}。");
        }
        catch (System.IO.IOException)
        {
            return new ProxyProbeResult(false, null, "代理端口未返回可用的 HTTP CONNECT 响应。");
        }
    }
}
