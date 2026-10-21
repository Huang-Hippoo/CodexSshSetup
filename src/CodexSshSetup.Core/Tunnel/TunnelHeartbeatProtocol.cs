using System.Text;

namespace CodexSshSetup.Core.Tunnel;

public static class TunnelHeartbeatProtocol
{
    public const string Ready = "CODEX_SSH_TUNNEL_READY";
    public const string Heartbeat = "CODEX_SSH_HEARTBEAT";
    public const string Acknowledgement = "CODEX_SSH_HEARTBEAT_ACK";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    // The remote watchdog owns only its parent SSH session. No files, services,
    // routes, or other sessions are changed. An orphaned forward expires in 40s.
    public static string RemoteCommand { get; } = BuildRemoteCommand();

    private static string BuildRemoteCommand()
    {
        var script = $$"""
            owner=$PPID
            uid=$(id -u)
            if [ "$(ps -p "$owner" -o comm=)" != sshd ] ||
               [ "$(ps -p "$owner" -o uid= | tr -d ' ')" != "$uid" ]; then
                echo 'Cannot verify tunnel session ownership' >&2
                exit 1
            fi
            cleanup() {
                if [ "$(ps -p "$$" -o ppid= | tr -d ' ')" = "$owner" ] &&
                   [ "$(ps -p "$owner" -o comm=)" = sshd ] &&
                   [ "$(ps -p "$owner" -o uid= | tr -d ' ')" = "$uid" ]; then
                    kill -TERM "$owner" 2>/dev/null || :
                fi
            }
            trap cleanup EXIT
            printf '%s\n' '{{Ready}}'
            while IFS= read -r -t 40 line; do
                [ "$line" = '{{Heartbeat}}' ] || break
                printf '%s\n' '{{Acknowledgement}}'
            done
            """;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"exec /bin/bash --noprofile --norc -c \"$(printf %s '{encoded}' | base64 -d)\"";
    }
}
