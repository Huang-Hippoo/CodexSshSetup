namespace CodexSshSetup.Core.Models;

public sealed record ServerProfile(
    string Host,
    string UserName,
    int Port,
    string Alias,
    int RemoteProxyPort = 17890);
