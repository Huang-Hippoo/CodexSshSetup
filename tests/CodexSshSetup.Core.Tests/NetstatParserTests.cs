using CodexSshSetup.Core.Parsing;

namespace CodexSshSetup.Core.Tests;

public sealed class NetstatParserTests
{
    private const string Netstat = """
          TCP    127.0.0.1:7890         0.0.0.0:0              LISTENING       4321
          TCP    0.0.0.0:7891           0.0.0.0:0              LISTENING       4321
          TCP    127.0.0.1:5555         0.0.0.0:0              LISTENING       9999
          TCP    127.0.0.1:6000         127.0.0.1:6001         ESTABLISHED     4321
        """;

    [Fact]
    public void Finds_only_listening_ports_owned_by_the_target_process()
    {
        var ports = NetstatParser.FindListeningPorts(Netstat, [4321]);

        Assert.Equal(new[] { 7890, 7891 }, ports);
    }
}
