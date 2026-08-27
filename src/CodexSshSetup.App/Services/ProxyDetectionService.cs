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
    private static readonly int[] CommonPorts = [7890, 7891, 1080];

    public async Task<ProxyDetectionResult> DetectAsync(
        int? preferredPort, CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName("iKuuuVPNCore");
        try
        {
            if (processes.Length == 0)
            {
                return new ProxyDetectionResult(false, null, false, "未检测到 iKuuuVPNCore.exe。");
            }

            var candidates = new List<int>();
            if (preferredPort is > 0 and <= 65535) candidates.Add(preferredPort.Value);
            candidates.Add(7890);
            candidates.AddRange(await FindProcessPortsAsync(processes.Select(process => process.Id).ToArray(), cancellationToken));
            candidates.AddRange(CommonPorts);
            foreach (var port in candidates.Distinct())
            {
                var probe = await ProbeAsync(port, cancellationToken);
                if (probe.Success)
                {
                    return new ProxyDetectionResult(true, port, true, $"代理端口 {port} 的 HTTPS CONNECT 可用。");
                }
            }

            return new ProxyDetectionResult(true, null, false,
                "iKuuuVPN 正在运行，但常用 HTTP/mixed 端口未通过 CONNECT；可手动输入端口重试。");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
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
    }
}
