namespace CodexSshSetup.Core.Parsing;

public static class NetstatParser
{
    public static IReadOnlyList<int> FindListeningPorts(string output, IReadOnlyCollection<int> processIds)
    {
        var processSet = processIds.ToHashSet();
        var ports = new HashSet<int>();
        foreach (var line in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(parts[^1], out var processId) || !processSet.Contains(processId)) continue;
            if (!TryParseLoopbackCapablePort(parts[1], out var port)) continue;
            ports.Add(port);
        }
        return ports.Order().ToArray();
    }

    private static bool TryParseLoopbackCapablePort(string endpoint, out int port)
    {
        port = 0;
        var separator = endpoint.LastIndexOf(':');
        if (separator < 1 || !int.TryParse(endpoint[(separator + 1)..], out port)) return false;
        var address = endpoint[..separator].Trim('[', ']');
        return port is >= 1 and <= 65535 && address is "127.0.0.1" or "0.0.0.0" or "::" or "::1";
    }
}
