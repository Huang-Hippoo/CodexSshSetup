using System.Security.Cryptography;

namespace CodexSshSetup.Core.Ssh;

public static class HostTrust
{
    public static string Name(string host) => "codex-gateway-" +
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(host.ToLowerInvariant())))[..24].ToLowerInvariant();

    public static string PinPath(string keyPath) => keyPath + ".known_hosts";

    public static string[] Keys(string text, string host, bool includePorts)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0].StartsWith('#') || parts[0].StartsWith('@')) continue;
            var matches = parts[0].Split(',').Any(name =>
                name.Equals(host, StringComparison.OrdinalIgnoreCase) ||
                (includePorts && name.StartsWith("[" + host + "]:", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(name[(host.Length + 3)..], out var port) && port is > 0 and <= 65535));
            if (!matches) continue;
            try
            {
                if (Convert.FromBase64String(parts[2]).Length > 0)
                    keys.Add(parts[1] + " " + parts[2]);
            }
            catch (FormatException) { }
        }
        return keys.ToArray();
    }

    public static string Fingerprint(string key) => "SHA256:" + Convert.ToBase64String(
        SHA256.HashData(Convert.FromBase64String(key.Split(' ')[1]))).TrimEnd('=');
}
